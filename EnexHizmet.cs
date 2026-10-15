// Enex Hizmet — kasa bilgisayarında arka planda çalışan küçük servis (C# 5 / .NET 4, PowerShell Add-Type ile derlenir)
//  * /okc/*   : ÖKC (yazar kasa POS) köprüsü — yalnızca bu bilgisayardan (127.0.0.1) erişilir
//  * /admin/* : ayarlar — yalnızca bu bilgisayardan
//  * /sync/*  : ağdaki diğer kasalarla ortak veri — yalnızca "ana kasa" rolünde ve ağ anahtarıyla
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public static class EnexHizmet
{
    public const string Surum = "1.1";
    static string dir, iniPath, logPath;
    static Dictionary<string, string> cfg = new Dictionary<string, string>();
    static readonly object cfgLock = new object();
    static TcpListener listener;
    static volatile bool running;

    // ---------------- ayarlar ----------------
    static void LoadCfg()
    {
        lock (cfgLock)
        {
            cfg = new Dictionary<string, string>();
            if (File.Exists(iniPath))
                foreach (var line in File.ReadAllLines(iniPath, Encoding.UTF8))
                {
                    var i = line.IndexOf('=');
                    if (i > 0 && !line.StartsWith("#")) cfg[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            if (!cfg.ContainsKey("port")) cfg["port"] = "8765";
            if (!cfg.ContainsKey("rol")) cfg["rol"] = "tek";
            if (!cfg.ContainsKey("anahtar")) cfg["anahtar"] = "";
        }
    }
    static void SaveCfg()
    {
        lock (cfgLock)
        {
            var sb = new StringBuilder("# Enex Hizmet ayarlari (programdan degistirilir)\r\n");
            foreach (var kv in cfg) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
            File.WriteAllText(iniPath, sb.ToString(), new UTF8Encoding(false));
        }
    }
    static string Cfg(string k) { lock (cfgLock) { string v; return cfg.TryGetValue(k, out v) ? v : ""; } }
    public static void Log(string m)
    {
        try
        {
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 512 * 1024) File.Delete(logPath);
            File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + m + "\r\n", new UTF8Encoding(false));
        }
        catch { }
    }

    // ---------------- ortak veri deposu ----------------
    class Ent { public long S; public long V; public string D; }   // D == null → silinmiş
    static readonly Dictionary<string, Ent> docs = new Dictionary<string, Ent>(StringComparer.Ordinal);
    static readonly Dictionary<string, KeyValuePair<string, long>> leases = new Dictionary<string, KeyValuePair<string, long>>();
    static readonly object db = new object();
    static long seq = 0;
    static string journal;
    static StreamWriter jw;
    static long liveBytes = 0, journalBytes = 0;

    static void LoadStore()
    {
        var vdir = Path.Combine(dir, "veri");
        Directory.CreateDirectory(vdir);
        journal = Path.Combine(vdir, "enex-ortak.jrn");
        if (File.Exists(journal))
        {
            using (var r = new StreamReader(journal, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    var p = line.Split(new[] { '\t' }, 4);
                    if (p.Length < 4) continue;   // yarım kalmış son satır
                    long s, v; if (!long.TryParse(p[0], out s) || !long.TryParse(p[1], out v)) continue;
                    docs[p[2]] = new Ent { S = s, V = v, D = p[3] == "-" ? null : p[3] };
                    if (s > seq) seq = s;
                }
            }
        }
        Compact();
    }
    static void Compact()
    {
        if (jw != null) { jw.Dispose(); jw = null; }
        var tmp = journal + ".tmp";
        long n = 0;
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            foreach (var kv in SortedBySeq(0, int.MaxValue))
            {
                var l = kv.Value.S + "\t" + kv.Value.V + "\t" + kv.Key + "\t" + (kv.Value.D ?? "-");
                w.WriteLine(l); n += l.Length + 2;
            }
        if (File.Exists(journal)) { var bak = journal + ".bak"; if (File.Exists(bak)) File.Delete(bak); File.Move(journal, bak); }
        File.Move(tmp, journal);
        liveBytes = n; journalBytes = n;
        jw = new StreamWriter(new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        jw.AutoFlush = true;
    }
    static List<KeyValuePair<string, Ent>> SortedBySeq(long since, int max)
    {
        var list = new List<KeyValuePair<string, Ent>>();
        foreach (var kv in docs) if (kv.Value.S > since) list.Add(kv);
        list.Sort(delegate (KeyValuePair<string, Ent> a, KeyValuePair<string, Ent> b) { return a.Value.S.CompareTo(b.Value.S); });
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        return list;
    }
    static Ent Write(string path, string data, out bool conflict, long ifVer)
    {
        conflict = false;
        lock (db)
        {
            Ent cur; docs.TryGetValue(path, out cur);
            long curV = (cur != null && cur.D != null) ? cur.V : 0;
            if (ifVer >= 0 && ifVer != curV) { conflict = true; return cur; }
            var e = new Ent { S = ++seq, V = (cur != null ? cur.V : 0) + 1, D = data };
            docs[path] = e;
            var l = e.S + "\t" + e.V + "\t" + path + "\t" + (data ?? "-");
            jw.WriteLine(l); journalBytes += l.Length + 2;
            if (journalBytes > 64L * 1024 * 1024 && journalBytes > liveBytes * 3) Compact();
            Monitor.PulseAll(db);
            return e;
        }
    }

    // ---------------- HTTP (basit, kütüphanesiz) ----------------
    class Req { public string Method, Path, Query; public Dictionary<string, string> H = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); public byte[] Body; public bool Local; public IPAddress Ip; }
    static Dictionary<string, string> Qs(string q)
    {
        var d = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(q)) return d;
        foreach (var part in q.Split('&'))
        {
            var i = part.IndexOf('=');
            if (i < 0) d[Uri.UnescapeDataString(part)] = "";
            else d[Uri.UnescapeDataString(part.Substring(0, i))] = Uri.UnescapeDataString(part.Substring(i + 1).Replace('+', ' '));
        }
        return d;
    }
    static string Js(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            if (c == '"') sb.Append("\\\""); else if (c == '\\') sb.Append("\\\\");
            else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
    static Req ReadReq(NetworkStream ns)
    {
        var head = new List<byte>(); int b, n = 0;
        while ((b = ns.ReadByte()) >= 0)
        {
            head.Add((byte)b);
            if (head.Count >= 4 && head[head.Count - 1] == 10 && head[head.Count - 2] == 13 && head[head.Count - 3] == 10 && head[head.Count - 4] == 13) break;
            if (++n > 32768) return null;
        }
        if (b < 0) return null;
        var lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
        var rl = lines[0].Split(' ');
        if (rl.Length < 2) return null;
        var r = new Req { Method = rl[0].ToUpperInvariant() };
        var qi = rl[1].IndexOf('?');
        r.Path = qi < 0 ? rl[1] : rl[1].Substring(0, qi); r.Query = qi < 0 ? "" : rl[1].Substring(qi + 1);
        for (int i = 1; i < lines.Length; i++) { var c = lines[i].IndexOf(':'); if (c > 0) r.H[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim(); }
        int len = 0; string cl; if (r.H.TryGetValue("Content-Length", out cl)) int.TryParse(cl, out len);
        if (len > 200 * 1024 * 1024) return null;
        r.Body = new byte[len]; int got = 0;
        while (got < len) { var k = ns.Read(r.Body, got, len - got); if (k <= 0) return null; got += k; }
        return r;
    }
    static void Send(NetworkStream ns, int code, string body, string ctype)
    {
        var bb = Encoding.UTF8.GetBytes(body ?? "");
        var st = code == 200 ? "OK" : code == 204 ? "No Content" : code == 401 ? "Unauthorized" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : code == 409 ? "Conflict" : "Error";
        var h = "HTTP/1.1 " + code + " " + st + "\r\nContent-Type: " + (ctype ?? "application/json; charset=utf-8") + "\r\nContent-Length: " + bb.Length +
                "\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Headers: X-Enex-Key, Content-Type\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Private-Network: true\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        var hb = Encoding.ASCII.GetBytes(h);
        ns.Write(hb, 0, hb.Length); ns.Write(bb, 0, bb.Length); ns.Flush();
    }

    public static void Run(string baseDir)
    {
        dir = baseDir; Okc.Dir = baseDir; iniPath = Path.Combine(dir, "enex-hizmet.ini"); logPath = Path.Combine(dir, "enex-hizmet.log");
        LoadCfg(); LoadStore();
        int port; if (!int.TryParse(Cfg("port"), out port)) port = 8765;
        listener = new TcpListener(IPAddress.Any, port);
        try { listener.Start(); }
        catch (Exception ex) { Log("Port acilamadi " + port + ": " + ex.Message); listener = new TcpListener(IPAddress.Loopback, port); listener.Start(); }
        running = true;
        Log("Enex Hizmet " + Surum + " basladi, port " + port + ", rol " + Cfg("rol") + ", kayit " + docs.Count);
        while (running)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); } catch { if (!running) break; continue; }
            ThreadPool.QueueUserWorkItem(Handle, c);
        }
    }
    public static void Stop() { running = false; try { listener.Stop(); } catch { } try { if (jw != null) jw.Dispose(); } catch { } }

    static void Handle(object o)
    {
        var c = (TcpClient)o;
        try
        {
            c.ReceiveTimeout = 30000; c.SendTimeout = 30000;
            var ns = c.GetStream();
            var r = ReadReq(ns); if (r == null) { c.Close(); return; }
            var ep = (IPEndPoint)c.Client.RemoteEndPoint; r.Ip = ep.Address; r.Local = IPAddress.IsLoopback(ep.Address);
            if (r.Method == "OPTIONS") { Send(ns, 204, "", "text/plain"); c.Close(); return; }
            string res; int code = 200;
            try { res = Route(r, ref code); }
            catch (Exception ex) { code = 500; res = "{\"ok\":false,\"err\":" + Js(ex.Message) + "}"; Log("Hata " + r.Path + ": " + ex); }
            Send(ns, code, res, null);
        }
        catch (Exception ex) { Log("Baglanti hatasi: " + ex.Message); }
        finally { try { c.Close(); } catch { } }
    }

    // Uzaktan destek: yalnızca PROGRAM KLASÖRÜNDEKİ Alpemix*.exe açılır (tarayıcı oraya dosya yazamaz).
    // İnternet sitelerinden gelen istekler (Origin başlığı "null" dışında) reddedilir.
    static string AlpemixYol()
    {
        var p = Path.Combine(dir, "Alpemix.exe"); if (File.Exists(p)) return p;
        try { foreach (var f in Directory.GetFiles(dir, "Alpemix*.exe")) return f; } catch { }
        return null;
    }
    static string Alpemix(Req r, ref int code)
    {
        string o; if (r.H.TryGetValue("Origin", out o) && !string.IsNullOrEmpty(o) && o != "null") { code = 403; return "{\"ok\":false,\"err\":\"izin yok\"}"; }
        var p = AlpemixYol();
        if (r.Method != "POST") return "{\"ok\":true,\"var\":" + (p != null ? "true" : "false") + ",\"klasor\":" + Js(dir) + "}";
        if (p == null) { code = 404; return "{\"ok\":false,\"err\":\"yok\",\"klasor\":" + Js(dir) + "}"; }
        var psi = new System.Diagnostics.ProcessStartInfo(p); psi.UseShellExecute = true; psi.WorkingDirectory = dir;
        System.Diagnostics.Process.Start(psi); Log("Alpemix acildi: " + p);
        return "{\"ok\":true}";
    }

    static bool KeyOk(Req r)
    {
        var k = Cfg("anahtar"); string g;
        if (string.IsNullOrEmpty(k) || !r.H.TryGetValue("X-Enex-Key", out g) || g.Length != k.Length) return false;
        int diff = 0; for (int i = 0; i < k.Length; i++) diff |= k[i] ^ g[i];
        return diff == 0;
    }

    static string Route(Req r, ref int code)
    {
        var q = Qs(r.Query);
        if (r.Path == "/ping")
            return "{\"ok\":true,\"app\":\"enex-hizmet\",\"v\":" + Js(Surum) + ",\"rol\":" + Js(Cfg("rol")) + ",\"ad\":" + Js(Environment.MachineName) + ",\"seq\":" + seq + ",\"anahtar\":" + (string.IsNullOrEmpty(Cfg("anahtar")) ? "false" : "true") + "}";

        // ---- yalnızca bu bilgisayar ----
        if (r.Path.StartsWith("/admin/") || r.Path.StartsWith("/okc/"))
        {
            if (!r.Local) { code = 403; return "{\"ok\":false,\"err\":\"yalnizca bu bilgisayardan\"}"; }
            if (r.Path == "/admin/ayar")
            {
                if (r.Method == "POST")
                {
                    lock (cfgLock)
                    {
                        foreach (var k in new[] { "rol", "anahtar", "magaza" }) if (q.ContainsKey(k)) cfg[k] = q[k].Replace("\r", "").Replace("\n", "");
                    }
                    SaveCfg(); Log("Ayar degisti: rol " + Cfg("rol"));
                }
                return "{\"ok\":true,\"rol\":" + Js(Cfg("rol")) + ",\"port\":" + Js(Cfg("port")) + ",\"anahtar\":" + Js(Cfg("anahtar")) + ",\"ipler\":[" + string.Join(",", LocalIps().ConvertAll(Js).ToArray()) + "],\"kayit\":" + docs.Count + ",\"seq\":" + seq + "}";
            }
            if (r.Path == "/admin/yukle")   // ana kasa ilk kurulum: bu bilgisayardaki verileri depoya yükler (depo boşken)
            {
                if (Cfg("rol") != "ana") { code = 409; return "{\"ok\":false,\"err\":\"once ana kasa yapin\"}"; }
                var force = q.ContainsKey("zorla");
                lock (db) if (docs.Count > 0 && !force) { code = 409; return "{\"ok\":false,\"err\":\"ortak depo bos degil\",\"kayit\":" + docs.Count + "}"; }
                var txt = Encoding.UTF8.GetString(r.Body); int n = 0; bool cf;
                foreach (var line in txt.Split('\n'))
                {
                    var t = line.TrimEnd('\r'); var i = t.IndexOf('\t'); if (i <= 0) continue;
                    Write(t.Substring(0, i), t.Substring(i + 1), out cf, -1); n++;
                }
                Log("Ilk yukleme: " + n + " kayit");
                return "{\"ok\":true,\"n\":" + n + "}";
            }
            if (r.Path == "/admin/alpemix") return Alpemix(r, ref code);
            if (r.Path.StartsWith("/okc/")) return Okc.Route(r.Path, q, r.Body == null ? "" : Encoding.UTF8.GetString(r.Body), ref code);
            code = 404; return "{\"ok\":false}";
        }

        // ---- ağ: ortak veri ----
        if (r.Path.StartsWith("/sync/"))
        {
            if (Cfg("rol") != "ana") { code = 409; return "{\"ok\":false,\"err\":\"bu bilgisayar ana kasa degil\"}"; }
            if (!KeyOk(r)) { code = 401; return "{\"ok\":false,\"err\":\"ag anahtari yanlis\"}"; }
            if (r.Path == "/sync/changes")
            {
                long since = 0; long.TryParse(q.ContainsKey("since") ? q["since"] : "0", out since);
                int wait = 0; int.TryParse(q.ContainsKey("wait") ? q["wait"] : "0", out wait); wait = Math.Max(0, Math.Min(25, wait));
                var sb = new StringBuilder(); bool more = false; long last = since;
                lock (db)
                {
                    if (since > seq) since = 0;   // depo sıfırlanmış: baştan al
                    if (seq <= since && wait > 0) Monitor.Wait(db, wait * 1000);
                    var list = SortedBySeq(since, 3000);
                    more = list.Count == 3000;
                    sb.Append("{\"ok\":true,\"seq\":").Append(seq).Append(",\"ch\":[");
                    bool first = true;
                    foreach (var kv in list)
                    {
                        if (!first) sb.Append(','); first = false;
                        sb.Append("{\"s\":").Append(kv.Value.S).Append(",\"v\":").Append(kv.Value.V).Append(",\"p\":").Append(Js(kv.Key)).Append(",\"d\":").Append(kv.Value.D ?? "null").Append('}');
                        last = kv.Value.S;
                    }
                    sb.Append("],\"last\":").Append(last).Append(",\"more\":").Append(more ? "true" : "false").Append('}');
                }
                return sb.ToString();
            }
            var path = q.ContainsKey("p") ? q["p"] : "";
            if (path.Length == 0 || path.Length > 400 || path.IndexOfAny(new[] { '\t', '\n', '\r', '"', '\\' }) >= 0) { code = 400; return "{\"ok\":false,\"err\":\"yol\"}"; }
            if (r.Path == "/sync/get")
            {
                lock (db) { Ent e; docs.TryGetValue(path, out e); return "{\"ok\":true,\"v\":" + (e != null && e.D != null ? e.V : 0) + ",\"d\":" + (e != null && e.D != null ? e.D : "null") + "}"; }
            }
            if (r.Path == "/sync/put" && r.Method == "POST")
            {
                long ifv = -1; if (q.ContainsKey("if")) long.TryParse(q["if"], out ifv);
                string data = q.ContainsKey("del") ? null : Encoding.UTF8.GetString(r.Body);
                if (data != null && (data.Length == 0 || data.IndexOf('\n') >= 0 || data[0] != '{')) { code = 400; return "{\"ok\":false,\"err\":\"govde\"}"; }
                bool conflict; var e = Write(path, data, out conflict, ifv);
                if (conflict) { code = 409; return "{\"ok\":false,\"v\":" + (e != null && e.D != null ? e.V : 0) + ",\"d\":" + (e != null && e.D != null ? e.D : "null") + "}"; }
                return "{\"ok\":true,\"v\":" + e.V + ",\"s\":" + e.S + "}";
            }
            if (r.Path == "/sync/lease" && r.Method == "POST")
            {
                var h = q.ContainsKey("h") ? q["h"] : ""; long ttl = 30000; if (q.ContainsKey("ttl")) long.TryParse(q["ttl"], out ttl);
                ttl = Math.Max(500, Math.Min(600000, ttl));
                var now = DateTime.UtcNow.Ticks / 10000;
                lock (db)
                {
                    KeyValuePair<string, long> cur;
                    if (leases.TryGetValue(path, out cur) && cur.Value > now && cur.Key != h) return "{\"ok\":false,\"until\":" + cur.Value + "}";
                    leases[path] = new KeyValuePair<string, long>(h, now + ttl);
                    if (leases.Count > 5000) { var old = new List<string>(); foreach (var x in leases) if (x.Value.Value < now) old.Add(x.Key); foreach (var k in old) leases.Remove(k); }
                    return "{\"ok\":true,\"until\":" + (now + ttl) + "}";
                }
            }
        }
        code = 404; return "{\"ok\":false,\"err\":\"yok\"}";
    }
    static List<string> LocalIps()
    {
        var l = new List<string>();
        try { foreach (var a in Dns.GetHostAddresses(Dns.GetHostName())) if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)) l.Add(a.ToString()); } catch { }
        return l;
    }
}

// ---------------- ÖKC köprüsü ----------------
// Her marka için sürücü: "sim" (simülatör) hazır. Gerçek markalar, üreticinin entegrasyon kitiyle yazılan
// surucu\<marka>.dll içindeki "EnexOkcSurucu" sınıfından yüklenir (metot: string Islem(string ip, int port, string tip, string istekJson)).
public static class Okc
{
    public static string Route(string path, Dictionary<string, string> q, string body, ref int code)
    {
        string ip = q.ContainsKey("ip") ? q["ip"] : "", drv = q.ContainsKey("drv") ? q["drv"] : "sim";
        int port = 0; int.TryParse(q.ContainsKey("port") ? q["port"] : "0", out port);
        if (path == "/okc/test")
        {
            if (drv == "sim") return "{\"ok\":true,\"ms\":1,\"msg\":\"Simulator hazir\"}";
            if (ip.Length == 0 || port <= 0) { return "{\"ok\":false,\"err\":\"IP adresi ve port girin\"}"; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var t = new TcpClient())
                {
                    var ar = t.BeginConnect(ip, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(3000)) return "{\"ok\":false,\"err\":\"Cihaz yanit vermedi (3 sn). IP / port dogru mu, cihaz ayni agda ve acik mi?\"}";
                    t.EndConnect(ar);
                }
                var surucu = SurucuVar(drv);
                return "{\"ok\":true,\"ms\":" + sw.ElapsedMilliseconds + ",\"surucu\":" + (surucu ? "true" : "false") + ",\"msg\":\"Cihaza ulasildi\"}";
            }
            catch (Exception ex) { return "{\"ok\":false,\"err\":" + JsS("Baglanilamadi: " + ex.Message) + "}"; }
        }
        if (path == "/okc/islem")
        {
            var tip = q.ContainsKey("tip") ? q["tip"] : "satis";
            if (drv == "sim")
            {
                decimal tutar = 0; decimal.TryParse(q.ContainsKey("tutar") ? q["tutar"] : "0", System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out tutar);
                Thread.Sleep(1500);
                if (decimal.Round(tutar * 100) % 100 == 13) return "{\"ok\":true,\"status\":\"red\",\"msg\":\"Yetersiz bakiye (simulator)\"}";
                var rnd = new Random();
                return "{\"ok\":true,\"status\":\"onay\",\"msg\":\"Onaylandi (simulator)\",\"auth\":\"" + rnd.Next(100000, 999999) + "\",\"rrn\":\"" + rnd.Next(10000000, 99999999) + "\",\"fisNo\":\"" + rnd.Next(1000, 9999) + "\"}";
            }
            var asm = SurucuYolu(drv);
            if (!File.Exists(asm)) return "{\"ok\":true,\"status\":\"red\",\"msg\":" + JsS(drv + " suruculu kurulu degil: uretici entegrasyon kiti gerekli (surucu\\" + drv + ".dll)") + "}";
            try
            {
                var a = System.Reflection.Assembly.LoadFrom(asm);
                var t = a.GetType("EnexOkcSurucu"); var o = Activator.CreateInstance(t);
                var m = t.GetMethod("Islem");
                var res = (string)m.Invoke(o, new object[] { ip, port, tip, body });
                return res;
            }
            catch (Exception ex) { EnexHizmet.Log("OKC surucu hatasi: " + ex); return "{\"ok\":true,\"status\":\"red\",\"msg\":" + JsS("Surucu hatasi: " + (ex.InnerException ?? ex).Message) + "}"; }
        }
        code = 404; return "{\"ok\":false}";
    }
    public static string Dir = "";
    static string SurucuYolu(string drv) { var sb = new StringBuilder(); foreach (var ch in drv) if (char.IsLetterOrDigit(ch)) sb.Append(ch); var clean = sb.ToString(); return Path.Combine(Path.Combine(Dir, "surucu"), clean + ".dll"); }
    static bool SurucuVar(string drv) { return File.Exists(SurucuYolu(drv)); }
    static string JsS(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ") + "\""; }
}
