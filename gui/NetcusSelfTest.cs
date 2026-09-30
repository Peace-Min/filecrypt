using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FileCrypt
{
    /// <summary>
    /// 근태관리 올리기·가져오기를 창 없이, 실제 코드 전체(NetcusJobs → NetcusGateway → NetcusService → WebView2)로
    /// 한 번 돌리고 결과를 JSON 으로 남긴다. 목업 사이트(tools\netcus-mock) 전용.
    ///   FileCrypt.exe --netcus-selftest 시나리오.json
    ///
    /// 실제 사이트·실제 설정에 닿지 않도록 FILECRYPT_NETCUS_MOCK(목업 포트)와 FILECRYPT_DATA_DIR(격리 폴더)가
    /// 둘 다 있을 때만 돈다. engine\tests\test-netcus-mock.ps1 이 부른다.
    ///
    /// 시나리오:
    ///   { "account": {"id":"..","pw":".."}, "op": "upload"|"download"|"login",
    ///     "files": [".."], "start": "2024-08-14", "chunk": 1000, "confirmOverwrite": true,
    ///     "days": 3, "outDir": "..", "clear": true,
    ///     "paceMs": 0, "submitTimeoutSec": 120, "readBaseSec": 30, "readPerDaySec": 15,
    ///     "result": "결과.json" }
    /// </summary>
    internal static class NetcusSelfTest
    {
        public static async Task<int> RunAsync(string scenarioPath)
        {
            var log = new List<string>();
            var result = new Dictionary<string, object>();
            string resultPath = null;
            int code = 0;
            try
            {
                if (NetcusHost.MockPort <= 0 || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppConfig.DirEnvVar)))
                    throw new InvalidOperationException("--netcus-selftest 는 " + NetcusHost.MockEnvVar + " 와 " + AppConfig.DirEnvVar
                                                        + " 가 둘 다 있을 때만 돈다(실제 사이트·설정 보호).");

                JsonElement sc;
                using (var doc = JsonDocument.Parse(File.ReadAllText(scenarioPath, Encoding.UTF8)))
                    sc = doc.RootElement.Clone();
                resultPath = Str(sc, "result", Path.ChangeExtension(scenarioPath, ".result.json"));

                var acc = sc.GetProperty("account");
                string id = acc.GetProperty("id").GetString(), pw = acc.GetProperty("pw").GetString();
                AppConfig.NetcusId = id;
                AppConfig.NetcusPassword = pw;

                NetcusGateway.SubmitTimeout = TimeSpan.FromSeconds(Num(sc, "submitTimeoutSec", 120));
                NetcusGateway.ReadBaseTimeout = TimeSpan.FromSeconds(Num(sc, "readBaseSec", 30));
                NetcusGateway.ReadPerDayTimeout = TimeSpan.FromSeconds(Num(sc, "readPerDaySec", 15));

                using (var gw = new NetcusGateway())
                {
                    gw.Progress += s => log.Add("  " + s);
                    gw.Logged += s => log.Add("  " + s);
                    var jobs = new NetcusJobs(gw, s => log.Add(s))
                    {
                        PaceMs = Num(sc, "paceMs", 0),
                        ConfirmOverwrite = msg => { log.Add("[덮어쓰기 확인] " + msg.Replace("\r\n", " / ")); return Bool(sc, "confirmOverwrite", false); }
                    };

                    var login = await gw.EnsureLoginAsync(id, pw);
                    result["login"] = login.Key;
                    result["loginSkipped"] = login.Value;
                    string op = Str(sc, "op", "login");
                    if (!login.Key) { result["error"] = "login"; }
                    else if (op == "upload")
                    {
                        var inputs = sc.GetProperty("files").EnumerateArray()
                                       .Select(f => new FileCryptJobs.PackInput { FullPath = f.GetString() }).ToList();
                        int n; long bytes; List<string> errs;
                        byte[] container = FileCryptJobs.BuildContainer(inputs, out n, out bytes, out errs);
                        var slots = NetcusPlan.Build(container, DateTime.Parse(Str(sc, "start", "2024-08-14")), Num(sc, "chunk", 200000));
                        // attempts > 1 = 같은 올리기 창에서 [올리기] 를 다시 누르는 것(같은 컨테이너, 같은 조각).
                        // 중간에 멈춘 뒤 이어 올리기가 이 경로다.
                        var attempts = new List<object>();
                        NetcusJobs.UploadResult r = null;
                        for (int a = 0; a < Math.Max(1, Num(sc, "attempts", 1)); a++)
                        {
                            if (a > 0) log.Add("---- 다시 올리기 " + (a + 1));
                            r = await jobs.UploadAsync(container, slots);
                            attempts.Add(new { outcome = r.Outcome.ToString(), written = r.Written, skipped = r.Skipped, verified = r.Verified, roundTrip = r.RoundTrip });
                            if (r.Outcome == NetcusJobs.UploadOutcome.Done) break;
                        }
                        result["attempts"] = attempts;
                        result["outcome"] = r.Outcome.ToString();
                        result["total"] = r.Total;
                        result["skipped"] = r.Skipped;
                        result["written"] = r.Written;
                        result["verified"] = r.Verified;
                        result["roundTrip"] = r.RoundTrip;
                        result["failedAt"] = r.FailedAt.HasValue ? r.FailedAt.Value.ToString("yyyy-MM-dd") : null;
                        result["failedWhy"] = r.FailedWhy;
                        result["backupFile"] = r.BackupFile;
                        result["slotTexts"] = slots.Select(s => s.Text).ToList();
                        result["dates"] = slots.Select(s => s.Date.ToString("yyyy-MM-dd")).ToList();
                        result["skipped52"] = r.Skipped52;
                        result["span"] = r.Span;
                    }
                    else if (op == "download")
                    {
                        var r = await jobs.DownloadAsync(DateTime.Parse(Str(sc, "start", "2024-08-14")), Num(sc, "days", 1),
                                                         Str(sc, "outDir", Path.GetTempPath()), Bool(sc, "clear", false));
                        result["ready"] = r.Ready;
                        result["why"] = r.Why;
                        if (r.Unpacked != null)
                        {
                            result["okCount"] = r.Unpacked.OkCount;
                            result["failedCount"] = r.Unpacked.FailedCount;
                            result["written"] = r.Unpacked.WrittenFiles;
                            result["targetDir"] = r.Unpacked.TargetDir;
                        }
                        result["clearTargets"] = r.ClearTargets;
                        result["cleared"] = r.Cleared;
                        result["clearAborted"] = r.ClearAborted;
                    }
                }
            }
            catch (Exception ex)
            {
                result["error"] = "exception";
                result["exception"] = ex.GetType().Name + ": " + ex.Message;
                code = 3;
            }
            result["log"] = log;
            try
            {
                File.WriteAllText(resultPath ?? (scenarioPath + ".result.json"),
                    JsonSerializer.Serialize(result, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    }), new UTF8Encoding(false));
            }
            catch { code = 4; }
            return code;
        }

        private static string Str(JsonElement e, string name, string def)
        {
            JsonElement v;
            return e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : def;
        }

        private static int Num(JsonElement e, string name, int def)
        {
            JsonElement v; int n;
            return e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out n) ? n : def;
        }

        private static bool Bool(JsonElement e, string name, bool def)
        {
            JsonElement v;
            if (!e.TryGetProperty(name, out v)) return def;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            return def;
        }
    }
}
