using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("ZonelyCoreRust", "Zonely", "1.0.0")]
    [Description("Poll-based command bridge for Rust: fetch signed jobs from your website, validate, queue, and execute.")]
    public class ZonelyCoreRust : CovalencePlugin
    {
        private ConfigData _cfg;

        public class ConfigData
        {
            [JsonProperty("Website URL (e.g. https://example.com)")]
            public string WebsiteUrl = "";

            [JsonProperty("API Key (shared with website)")]
            public string ApiKey = "";

            [JsonProperty("Server Token (unique per Rust server)")]
            public string ServerToken = "";

            [JsonProperty("Poll Interval Seconds")]
            public int PollIntervalSec = 8;

            [JsonProperty("Max Items Per Poll")]
            public int MaxPerPoll = 50;

            [JsonProperty("Delay Between Commands (seconds)")]
            public float DelayBetweenSec = 0.75f;

            [JsonProperty("Check Player Online Before Execute")]
            public bool CheckPlayerOnline = true;

            [JsonProperty("Debug Mode")]
            public bool Debug = false;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _cfg = Config.ReadObject<ConfigData>();
                if (_cfg == null) throw new Exception("null cfg");
            }
            catch
            {
                PrintWarning("Config invalid, generating defaults...");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _cfg = new ConfigData();
        }

        protected override void SaveConfig() => Config.WriteObject(_cfg, true);
        private class PullResponse
        {
            public List<Item> items;
            public class Item
            {
                public string id;
                public string username;
                public string command;
            }
        }

        private readonly Dictionary<string, List<string>> _queue = new Dictionary<string, List<string>>();
        private readonly object _queueLock = new object();
        private const string DATA_FILE = "queue.json";

        private void LoadQueue()
        {
            try
            {
                var path = Path.Combine(Interface.Oxide.DataDirectory, Name, DATA_FILE);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var d = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
                    if (d != null) foreach (var kv in d) _queue[kv.Key] = kv.Value;
                    LogInfo($"Loaded {_queue.Count} queued player(s).");
                }
            }
            catch (Exception e)
            {
                LogError($"LoadQueue error: {e.Message}");
            }
        }

        private void SaveQueue()
        {
            try
            {
                var dir = Path.Combine(Interface.Oxide.DataDirectory, Name);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, DATA_FILE);
                var json = JsonConvert.SerializeObject(_queue, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch (Exception e)
            {
                LogError($"SaveQueue error: {e.Message}");
            }
        }

        private void Enqueue(string steamId, IEnumerable<string> commands)
        {
            lock (_queueLock)
            {
                if (!_queue.ContainsKey(steamId)) _queue[steamId] = new List<string>();
                _queue[steamId].AddRange(commands.Where(c => !string.IsNullOrWhiteSpace(c)));
                SaveQueue();
            }
        }

        private void ProcessQueueFor(string steamId)
        {
            List<string> cmds = null;
            lock (_queueLock)
            {
                if (_queue.TryGetValue(steamId, out cmds))
                {
                    _queue.Remove(steamId);
                    SaveQueue();
                }
            }
            if (cmds == null || cmds.Count == 0) return;

            var p = players.FindPlayerById(steamId);
            var name = p?.Name ?? "Unknown";
            LogInfo($"Processing {cmds.Count} queued command(s) for {name} ({steamId})");
            ExecuteCommands(cmds, name);
        }
        #endregion

        #region Lifecycle
        private Timer _pollTimer;

        void Init()
        {
            if (!ValidateCfg())
            {
                PrintError("Configuration invalid. Please edit the config and reload the plugin.");
                return;
            }
            LoadQueue();
        }

        void OnServerInitialized()
        {
            StartPolling();
        }

        void Unload()
        {
            _pollTimer?.Destroy();
            SaveQueue();
        }

        void OnUserConnected(IPlayer p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.Id)) return;
                if (_cfg.CheckPlayerOnline && _queue.ContainsKey(p.Id))
                {
                    timer.Once(2f, () => ProcessQueueFor(p.Id));
                }
            }
            catch (Exception e)
            {
                LogError($"OnUserConnected error: {e.Message}");
            }
        }
        #endregion

        #region Polling
        private void StartPolling()
        {
            _pollTimer?.Destroy();
            _pollTimer = timer.Every(Math.Max(3, _cfg.PollIntervalSec), DoPoll);
            LogInfo($"Polling {_cfg.WebsiteUrl} every {_cfg.PollIntervalSec}s");
        }

        private void DoPoll()
        {
            if (string.IsNullOrWhiteSpace(_cfg.WebsiteUrl)) return;

            var url = $"{_cfg.WebsiteUrl.TrimEnd('/')}/api/zcr/pull";
            var body = $"token={Uri.EscapeDataString(_cfg.ServerToken)}&limit={_cfg.MaxPerPoll}";
            var headers = new Dictionary<string, string>
            {
                ["X-API-Key"] = _cfg.ApiKey,
                ["Content-Type"] = "application/x-www-form-urlencoded",
                ["Accept"] = "application/json"
            };

            if (_cfg.Debug) LogInfo($"Polling {url}");

            webrequest.Enqueue(url, body, (code, resp) =>
            {
                try
                {
                    if (code != 200 || string.IsNullOrWhiteSpace(resp))
                    {
                        if (_cfg.Debug) LogWarning($"Pull failed: HTTP {code}");
                        return;
                    }

                    string ts = webrequest.LastResponseHeaders?.ContainsKey("X-Timestamp") == true ? webrequest.LastResponseHeaders["X-Timestamp"] : null;
                    string sig = webrequest.LastResponseHeaders?.ContainsKey("X-Signature") == true ? webrequest.LastResponseHeaders["X-Signature"] : null;
                    if (!VerifyOptionalSignature(ts, resp, sig))
                    {
                        LogWarning("Signature verification failed. Skipping payload.");
                        return;
                    }

                    var data = JsonConvert.DeserializeObject<PullResponse>(resp);
                    if (data?.items == null || data.items.Count == 0) return;

                    var byUser = new Dictionary<string, List<PullResponse.Item>>();
                    foreach (var it in data.items)
                    {
                        if (it == null || string.IsNullOrWhiteSpace(it.command)) continue;
                        var uid = string.IsNullOrWhiteSpace(it.username) ? "" : it.username;
                        if (!byUser.ContainsKey(uid)) byUser[uid] = new List<PullResponse.Item>();
                        byUser[uid].Add(it);
                    }

                    var ackIds = new List<string>();

                    foreach (var kv in byUser)
                    {
                        var uid = kv.Key;
                        var list = kv.Value;
                        var commands = list.Select(x => x.command).ToList();

                        if (_cfg.CheckPlayerOnline && !string.IsNullOrEmpty(uid))
                        {
                            var pl = players.FindPlayerById(uid);
                            if (pl != null && pl.IsConnected)
                            {
                                var name = pl.Name ?? "Unknown";
                                LogInfo($"User online: {name} ({uid}) => executing {commands.Count}");
                                ExecuteCommands(commands, name);
                            }
                            else
                            {
                                LogInfo($"User offline: {uid} => queueing {commands.Count}");
                                Enqueue(uid, commands);
                            }
                        }
                        else
                        {
                            ExecuteCommands(commands, uid);
                        }

                        ackIds.AddRange(list.Select(x => x.id).Where(x => !string.IsNullOrWhiteSpace(x)));
                    }

                    if (ackIds.Count > 0) Ack(ackIds);
                }
                catch (Exception e)
                {
                    LogError($"Poll parse error: {e.Message}");
                }
            }, this, Oxide.Core.Libraries.RequestMethod.POST, headers, 10f);
        }

        private bool VerifyOptionalSignature(string ts, string body, string sig)
        {
            try
            {
                if (string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(ts)) return true;
                var message = $"{ts}.{body}";
                var expected = ToHex(HmacSha256(_cfg.ApiKey, message));
                var a = HexToBytes(expected);
                var b = HexToBytes(sig);
                if (a.Length != b.Length) return false;
                int diff = 0; for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
                return diff == 0;
            }
            catch { return false; }
        }

        private static byte[] HmacSha256(string key, string data)
        {
            using (var h = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(key ?? "")))
                return h.ComputeHash(Encoding.UTF8.GetBytes(data ?? ""));
        }
        private static string ToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
        private static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return new byte[0];
            var data = new byte[hex.Length / 2];
            for (int i = 0; i < data.Length; i++)
                data[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return data;
        }
        private void ExecuteCommands(List<string> commands, string usernameOrId)
        {
            if (commands == null || commands.Count == 0) return;
            if (_cfg.Debug) LogInfo($"Executing {commands.Count} command(s) for {usernameOrId}");

            float d = 0f;
            foreach (var cmd in commands)
            {
                var c = (cmd ?? "").Trim();
                if (string.IsNullOrEmpty(c)) continue;
                var delay = d;
                timer.Once(delay, () =>
                {
                    try
                    {
                        server.Command(c);
                        if (_cfg.Debug) LogInfo($"OK: {c}");
                    }
                    catch (Exception e)
                    {
                        LogError($"Exec fail: {c} => {e.Message}");
                    }
                });
                d += Math.Max(0.05f, _cfg.DelayBetweenSec);
            }
        }
        private void Ack(List<string> ids)
        {
            try
            {
                var url = $"{_cfg.WebsiteUrl.TrimEnd('/')}/api/zcr/ack";
                var obj = new { token = _cfg.ServerToken, ids = ids };
                var json = JsonConvert.SerializeObject(obj);
                var headers = new Dictionary<string, string>
                {
                    ["X-API-Key"] = _cfg.ApiKey,
                    ["Content-Type"] = "application/json",
                    ["Accept"] = "application/json"
                };
                if (_cfg.Debug) LogInfo($"Ack {ids.Count} ids");
                webrequest.Enqueue(url, json, (code, resp) =>
                {
                    if (_cfg.Debug) LogInfo($"Ack result: HTTP {code}");
                }, this, Oxide.Core.Libraries.RequestMethod.POST, headers, 10f);
            }
            catch (Exception e)
            {
                LogError($"Ack error: {e.Message}");
            }
        }
        [Command("zcr.status")]
        private void CmdStatus(IPlayer player, string cmd, string[] args)
        {
            if (!player.IsAdmin) return;
            player.Reply($"ZonelyCoreRust v1.0.0\n" +
                         $"- Website: {_cfg.WebsiteUrl}\n" +
                         $"- Poll: every {_cfg.PollIntervalSec}s, batch {_cfg.MaxPerPoll}\n" +
                         $"- CheckOnline: {_cfg.CheckPlayerOnline}\n" +
                         $"- Queue size: {_queue.Sum(kv => kv.Value.Count)}");
        }

        [Command("zcr.poll")]
        private void CmdPoll(IPlayer player, string cmd, string[] args)
        {
            if (!player.IsAdmin) return;
            DoPoll();
            player.Reply("Polling now...");
        }

        [Command("zcr.debug")]
        private void CmdDebug(IPlayer player, string cmd, string[] args)
        {
            if (!player.IsAdmin) return;
            _cfg.Debug = !_cfg.Debug;
            SaveConfig();
            player.Reply("Debug: " + (_cfg.Debug ? "ON" : "OFF"));
        }
        private void LogInfo(string m) => Puts($"[ZonelyCoreRust] {m}");
        private void LogWarning(string m) => PrintWarning($"[ZonelyCoreRust] {m}");
        private void LogError(string m) => PrintError($"[ZonelyCoreRust] {m}");
        private bool ValidateCfg()
        {
            var ok = true;
            if (string.IsNullOrWhiteSpace(_cfg.WebsiteUrl))
            {
                PrintError("Website URL must be set and use HTTPS in production.");
                ok = false;
            }
            if (string.IsNullOrWhiteSpace(_cfg.ApiKey))
            {
                PrintError("API Key must be set.");
                ok = false;
            }
            if (string.IsNullOrWhiteSpace(_cfg.ServerToken))
            {
                PrintError("Server Token must be set.");
                ok = false;
            }
            return ok;
        }
    }
}
