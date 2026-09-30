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
                JsonElement sc;
                using (var doc = JsonDocument.Parse(File.ReadAllText(scenarioPath, Encoding.UTF8)))
                    sc = doc.RootElement.Clone();
                resultPath = Str(sc, "result", Path.ChangeExtension(scenarioPath, ".result.json"));
                string op0 = Str(sc, "op", "login");

                // "plan" 은 읽기만 한다(기록·비우기 없음) - 실제 사이트에서 근태 읽기와 날짜 고르기를 확인할 때 쓴다.
                // 그때는 저장된 계정을 그대로 쓴다. 그 밖의 op 는 목업 + 격리 폴더에서만 돈다.
                bool readOnly = op0 == "plan";
                // 실제 사이트에 쓰는 것은 사람이 날짜를 적어 허락했을 때만(allowDates). 그 밖의 날짜에는 쓰지 않는다.
                bool realWriteAllowed = NetcusHost.MockPort <= 0 && AllowedDates(sc) != null;
                if (!readOnly && !realWriteAllowed && (NetcusHost.MockPort <= 0 || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppConfig.DirEnvVar))))
                    throw new InvalidOperationException("--netcus-selftest 는 " + NetcusHost.MockEnvVar + " 와 " + AppConfig.DirEnvVar
                                                        + " 가 둘 다 있을 때만 돈다(실제 사이트·설정 보호). 읽기만 하는 op=plan 은 예외.");

                string id, pw;
                JsonElement acc;
                if (sc.TryGetProperty("account", out acc))
                {
                    id = acc.GetProperty("id").GetString(); pw = acc.GetProperty("pw").GetString();
                    AppConfig.NetcusId = id;
                    AppConfig.NetcusPassword = pw;
                }
                else
                {
                    id = AppConfig.NetcusId; pw = AppConfig.NetcusPassword;
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(pw)) throw new InvalidOperationException("저장된 근태관리 계정이 없습니다.");
                }
                result["site"] = NetcusHost.MockPort > 0 ? "mock" : "real";

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
                        AllowedDates = AllowedDates(sc),
                        ConfirmOverwrite = msg => { log.Add("[덮어쓰기 확인] " + msg.Replace("\r\n", " / ")); return Bool(sc, "confirmOverwrite", false); }
                    };

                    var login = await gw.EnsureLoginAsync(id, pw);
                    result["login"] = login.Key;
                    result["loginSkipped"] = login.Value;
                    string op = Str(sc, "op", "login");
                    if (!login.Key) { result["error"] = "login"; }
                    else if (op == "plan")
                    {
                        // 읽기만: 근태 정보를 읽고, chunks 개 조각을 올린다면 어느 날에 넣을지만 계산한다.
                        DateTime start = DateTime.Parse(Str(sc, "start", "2024-08-12"));
                        int chunks = Num(sc, "chunks", 7);
                        var w = NetcusPlan.ReadWindow(start, chunks * 2 + 7);
                        var site = await gw.ReadDayInfosAsync(w.Key, w.Value);
                        var skipped = new List<string>();
                        var picked = NetcusPlan.PickDates(start, chunks, site, skipped);
                        var days = new List<object>();
                        foreach (var kv in site.OrderBy(k => k.Key))
                        {
                            // 페이지의 주간 합계가 "그 주 나머지 날 합계" 인지 검산: 같은 주 다른 날들의 시간을 직접 더한다.
                            int mine = 0; bool full = true;
                            DateTime ws = NetcusPlan.WeekStart(kv.Key);
                            for (int i = 0; i < 7; i++)
                            {
                                NetcusPlan.DayInfo o; DateTime x = ws.AddDays(i);
                                if (x == kv.Key) continue;
                                if (site.TryGetValue(x, out o)) mine += NetcusPlan.Hours(o.Status, o.Overtime); else full = false;
                            }
                            days.Add(new
                            {
                                date = kv.Key.ToString("yyyy-MM-dd ddd"),
                                status = kv.Value.Status, overtime = kv.Value.Overtime,
                                hours = NetcusPlan.Hours(kv.Value.Status, kv.Value.Overtime),
                                weekOthers = kv.Value.WeekOthers,
                                computedOthers = full ? (int?)mine : null,
                                contentChars = kv.Value.Content.Length   // 내용은 남기지 않는다
                            });
                        }
                        result["days"] = days;
                        result["picked"] = picked == null ? null : picked.Select(d => d.ToString("yyyy-MM-dd ddd")).ToList();
                        result["skipped52"] = skipped;
                    }
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

        /// <summary>시나리오의 allowDates(["yyyy-MM-dd", ...]). 없으면 null(제한 없음 - 목업 전용).</summary>
        private static HashSet<DateTime> AllowedDates(JsonElement sc)
        {
            JsonElement v;
            if (!sc.TryGetProperty("allowDates", out v) || v.ValueKind != JsonValueKind.Array) return null;
            var set = new HashSet<DateTime>();
            foreach (var d in v.EnumerateArray()) set.Add(DateTime.Parse(d.GetString()).Date);
            return set.Count > 0 ? set : null;
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
