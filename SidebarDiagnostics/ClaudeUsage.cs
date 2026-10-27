using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SidebarDiagnostics.Monitoring
{
    /// <summary>
    /// Una cuenta de Claude: apunta al .credentials.json de su carpeta de configuracion
    /// (CLAUDE_CONFIG_DIR). Los tokens nunca se copian: se leen del archivo en cada consulta.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class ClaudeAccount
    {
        [JsonProperty]
        public string ID { get; set; }

        [JsonProperty]
        public string Name { get; set; }

        [JsonProperty]
        public string CredentialsPath { get; set; }
    }

    public static class ClaudeAccountStore
    {
        public const string DEFAULTID = "default";

        private const string FILENAME = "claude-accounts.json";

        private static readonly object _lock = new object();

        public static string StoreFile
        {
            get { return Path.Combine(Utilities.Paths.LocalApp, FILENAME); }
        }

        public static string DefaultCredentialsPath
        {
            get
            {
                string _dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");

                if (string.IsNullOrWhiteSpace(_dir))
                {
                    _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                }

                return Path.Combine(_dir, ".credentials.json");
            }
        }

        /// <summary>Cuentas guardadas mas la cuenta por defecto de este equipo, si existe.</summary>
        public static ClaudeAccount[] Load()
        {
            List<ClaudeAccount> _accounts = new List<ClaudeAccount>();

            lock (_lock)
            {
                try
                {
                    if (File.Exists(StoreFile))
                    {
                        ClaudeAccount[] _saved = JsonConvert.DeserializeObject<ClaudeAccount[]>(File.ReadAllText(StoreFile));

                        if (_saved != null)
                        {
                            _accounts.AddRange(_saved.Where(a => a != null && !string.IsNullOrEmpty(a.ID)));
                        }
                    }
                }
                catch (Exception)
                {
                    // archivo corrupto: se ignora y se sigue con la cuenta por defecto
                }
            }

            if (_accounts.All(a => a.ID != DEFAULTID) && File.Exists(DefaultCredentialsPath))
            {
                _accounts.Insert(0, new ClaudeAccount() { ID = DEFAULTID, Name = GetEmail(DefaultCredentialsPath) ?? "Claude", CredentialsPath = DefaultCredentialsPath });
            }

            return _accounts.ToArray();
        }

        /// <summary>
        /// Correo de la cuenta, leido del .claude.json que Claude Code guarda junto a las credenciales
        /// (o en ~/.claude.json para la carpeta por defecto). Sin red; null si no se encuentra.
        /// </summary>
        public static string GetEmail(string credentialsPath)
        {
            try
            {
                string _dir = Path.GetDirectoryName(credentialsPath);

                List<string> _candidates = new List<string>() { Path.Combine(_dir, ".claude.json") };

                if (string.Equals(_dir.TrimEnd('\\', '/'), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), StringComparison.OrdinalIgnoreCase))
                {
                    _candidates.Insert(0, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"));
                }

                foreach (string _file in _candidates.Where(File.Exists))
                {
                    string _email = (string)JObject.Parse(File.ReadAllText(_file))["oauthAccount"]?["emailAddress"];

                    if (!string.IsNullOrEmpty(_email))
                    {
                        return _email;
                    }
                }
            }
            catch (Exception) { }

            return null;
        }

        /// <summary>Estado local de las credenciales (sin red) y cuando vence el token de acceso.</summary>
        public static ClaudeUsageStatus GetCredentialStatus(string credentialsPath, out string accessToken, out DateTime? expiresAt)
        {
            accessToken = null;
            expiresAt = null;

            try
            {
                if (string.IsNullOrEmpty(credentialsPath) || !File.Exists(credentialsPath))
                {
                    return ClaudeUsageStatus.NoCredentials;
                }

                JObject _oauth = (JObject)JObject.Parse(File.ReadAllText(credentialsPath))["claudeAiOauth"];

                accessToken = (string)_oauth["accessToken"];

                if (string.IsNullOrEmpty(accessToken))
                {
                    return ClaudeUsageStatus.NoCredentials;
                }

                long? _access = (long?)_oauth["expiresAt"];
                long? _refresh = (long?)_oauth["refreshTokenExpiresAt"];

                if (_access.HasValue)
                {
                    expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(_access.Value).UtcDateTime;
                }

                // Refresh token vencido: Claude Code ya no puede renovar solo, hay que volver a hacer /login.
                if (_refresh.HasValue && DateTimeOffset.FromUnixTimeMilliseconds(_refresh.Value) <= DateTimeOffset.UtcNow)
                {
                    return ClaudeUsageStatus.SessionExpired;
                }

                // Access token vencido: basta con abrir claude con esa cuenta para que lo renueve.
                if (expiresAt.HasValue && expiresAt.Value <= DateTime.UtcNow)
                {
                    return ClaudeUsageStatus.TokenExpired;
                }

                return ClaudeUsageStatus.Ok;
            }
            catch (Exception)
            {
                return ClaudeUsageStatus.NoCredentials;
            }
        }

        public static string DescribeStatus(string credentialsPath)
        {
            string _token;
            DateTime? _expires;

            switch (GetCredentialStatus(credentialsPath, out _token, out _expires))
            {
                case ClaudeUsageStatus.NoCredentials:
                    return "Sin credenciales";

                case ClaudeUsageStatus.SessionExpired:
                    return "Sesion vencida: ejecuta /login con esta cuenta";

                case ClaudeUsageStatus.TokenExpired:
                    return "Token vencido: abre claude con esta cuenta para renovarlo";

                default:
                    if (!_expires.HasValue)
                    {
                        return "Token valido";
                    }

                    TimeSpan _left = _expires.Value - DateTime.UtcNow;

                    return _left.TotalHours >= 1
                        ? string.Format("Token valido, se renueva en {0}h {1:00}m", (int)_left.TotalHours, _left.Minutes)
                        : string.Format("Token valido, se renueva en {0}m", Math.Max(1, _left.Minutes));
            }
        }

        public static ClaudeAccount Add(string credentialsPath, string name)
        {
            ClaudeAccount _account = new ClaudeAccount()
            {
                ID = Guid.NewGuid().ToString("N"),
                Name = name,
                CredentialsPath = credentialsPath
            };

            lock (_lock)
            {
                List<ClaudeAccount> _saved = LoadSaved();

                _saved.Add(_account);

                Save(_saved);
            }

            return _account;
        }

        public static void Remove(string id)
        {
            lock (_lock)
            {
                List<ClaudeAccount> _saved = LoadSaved();

                _saved.RemoveAll(a => a.ID == id);

                Save(_saved);
            }
        }

        private static List<ClaudeAccount> LoadSaved()
        {
            try
            {
                if (File.Exists(StoreFile))
                {
                    ClaudeAccount[] _saved = JsonConvert.DeserializeObject<ClaudeAccount[]>(File.ReadAllText(StoreFile));

                    if (_saved != null)
                    {
                        return _saved.Where(a => a != null).ToList();
                    }
                }
            }
            catch (Exception) { }

            return new List<ClaudeAccount>();
        }

        private static void Save(List<ClaudeAccount> accounts)
        {
            Directory.CreateDirectory(Utilities.Paths.LocalApp);

            File.WriteAllText(StoreFile, JsonConvert.SerializeObject(accounts, Formatting.Indented));
        }
    }

    public enum ClaudeUsageStatus : byte
    {
        Ok,
        NoCredentials,
        TokenExpired,
        SessionExpired,
        RateLimited,
        Error
    }

    public class ClaudeUsage
    {
        public ClaudeUsageStatus Status { get; set; } = ClaudeUsageStatus.Ok;

        public double? SessionPercent { get; set; }

        public DateTime? SessionReset { get; set; }

        public double? WeekPercent { get; set; }

        public DateTime? WeekReset { get; set; }
    }

    public static class ClaudeUsageClient
    {
        private const string URL = "https://api.anthropic.com/api/oauth/usage";

        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            HttpClient _client = new HttpClient() { Timeout = TimeSpan.FromSeconds(15) };
            _client.DefaultRequestHeaders.Add("anthropic-beta", "oauth-2025-04-20");
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("SidebarDiagnostics-ClaudeMonitor/1.0");

            return _client;
        }

        public static async Task<ClaudeUsage> FetchAsync(string credentialsPath)
        {
            // Se relee en cada consulta: Claude Code renueva el token en ese mismo archivo.
            string _token;
            DateTime? _expiresAt;

            ClaudeUsageStatus _status = ClaudeAccountStore.GetCredentialStatus(credentialsPath, out _token, out _expiresAt);

            if (_status != ClaudeUsageStatus.Ok)
            {
                return new ClaudeUsage() { Status = _status };
            }

            try
            {
                using (HttpRequestMessage _request = new HttpRequestMessage(HttpMethod.Get, URL))
                {
                    _request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

                    using (HttpResponseMessage _response = await _http.SendAsync(_request).ConfigureAwait(false))
                    {
                        if (_response.StatusCode == (HttpStatusCode)429)
                        {
                            return new ClaudeUsage() { Status = ClaudeUsageStatus.RateLimited };
                        }

                        if (_response.StatusCode == HttpStatusCode.Unauthorized || _response.StatusCode == HttpStatusCode.Forbidden)
                        {
                            return new ClaudeUsage() { Status = ClaudeUsageStatus.TokenExpired };
                        }

                        if (!_response.IsSuccessStatusCode)
                        {
                            return new ClaudeUsage() { Status = ClaudeUsageStatus.Error };
                        }

                        return Parse(JObject.Parse(await _response.Content.ReadAsStringAsync().ConfigureAwait(false)));
                    }
                }
            }
            catch (Exception)
            {
                return new ClaudeUsage() { Status = ClaudeUsageStatus.Error };
            }
        }

        // Endpoint no documentado: se leen las claves planas y, si faltan, el arreglo "limits".
        private static ClaudeUsage Parse(JObject json)
        {
            ClaudeUsage _usage = new ClaudeUsage();

            ReadBucket(json["five_hour"], out double? _sPct, out DateTime? _sReset);
            ReadBucket(json["seven_day"], out double? _wPct, out DateTime? _wReset);

            JArray _limits = json["limits"] as JArray;

            if (_limits != null)
            {
                if (!_sPct.HasValue)
                {
                    ReadLimit(_limits.FirstOrDefault(l => (string)l["kind"] == "session"), out _sPct, out _sReset);
                }

                if (!_wPct.HasValue)
                {
                    ReadLimit(_limits.FirstOrDefault(l => (string)l["kind"] == "weekly_all"), out _wPct, out _wReset);
                }
            }

            _usage.SessionPercent = _sPct;
            _usage.SessionReset = _sReset;
            _usage.WeekPercent = _wPct;
            _usage.WeekReset = _wReset;

            return _usage;
        }

        private static void ReadBucket(JToken bucket, out double? percent, out DateTime? reset)
        {
            percent = null;
            reset = null;

            if (bucket == null || bucket.Type != JTokenType.Object)
            {
                return;
            }

            percent = (double?)bucket["utilization"];
            reset = ParseDate(bucket["resets_at"]);
        }

        private static void ReadLimit(JToken limit, out double? percent, out DateTime? reset)
        {
            percent = null;
            reset = null;

            if (limit == null)
            {
                return;
            }

            percent = (double?)limit["percent"];
            reset = ParseDate(limit["resets_at"]);
        }

        private static DateTime? ParseDate(JToken token)
        {
            string _value = token == null || token.Type == JTokenType.Null ? null : (string)token;

            DateTimeOffset _date;

            if (_value != null && DateTimeOffset.TryParse(_value, out _date))
            {
                return _date.UtcDateTime;
            }

            return null;
        }
    }

    public class ClaudeMetric : BaseMetric
    {
        public ClaudeMetric(MetricKey key, bool numeric) : base(key, DataType.Percent)
        {
            _numeric = numeric;

            Text = "...";
        }

        public void SetPercent(double value)
        {
            Update(Math.Round(value));
        }

        public void SetText(string text)
        {
            Text = text;
        }

        public void SetAlert(bool alert)
        {
            if (IsAlert != alert)
            {
                IsAlert = alert;
            }
        }

        public override bool IsNumeric
        {
            get { return _numeric; }
        }

        private bool _numeric { get; set; }
    }

    public class ClaudeMonitor : BaseMonitor
    {
        private static readonly TimeSpan REFRESH = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan BACKOFF = TimeSpan.FromMinutes(5);

        public ClaudeMonitor(string id, string name, string credentialsPath, MetricConfig[] metrics, bool showName = true) : base(id, name, showName)
        {
            _credentialsPath = credentialsPath;

            List<iMetric> _metrics = new List<iMetric>();

            if (metrics.IsEnabled(MetricKey.ClaudeSession))
            {
                _session = new ClaudeMetric(MetricKey.ClaudeSession, true);
                _metrics.Add(_session);
            }

            if (metrics.IsEnabled(MetricKey.ClaudeWeek))
            {
                _week = new ClaudeMetric(MetricKey.ClaudeWeek, true);
                _metrics.Add(_week);
            }

            if (metrics.IsEnabled(MetricKey.ClaudeReset))
            {
                _resetMetric = new ClaudeMetric(MetricKey.ClaudeReset, false);
                _metrics.Add(_resetMetric);
            }

            Metrics = _metrics.ToArray();
        }

        public static IEnumerable<HardwareConfig> GetHardware()
        {
            return ClaudeAccountStore.Load().Select(a => new HardwareConfig()
            {
                ID = a.ID,
                Name = a.Name,
                ActualName = a.Name,
                Info = DescribeAccount(a)
            });
        }

        // Texto para la lista de cuentas en Ajustes: correo y estado del token.
        private static string DescribeAccount(ClaudeAccount account)
        {
            string _email = ClaudeAccountStore.GetEmail(account.CredentialsPath);
            string _status = ClaudeAccountStore.DescribeStatus(account.CredentialsPath);

            return string.IsNullOrEmpty(_email) ? _status : string.Format("{0} - {1}", _email, _status);
        }

        public static iMonitor[] GetInstances(HardwareConfig[] hardwareConfig, MetricConfig[] metrics, ConfigParam[] parameters)
        {
            bool _showName = parameters.GetValue<bool>(ParamKey.HardwareNames);

            ClaudeAccount[] _accounts = ClaudeAccountStore.Load();

            return (
                from acc in _accounts
                join c in hardwareConfig on acc.ID equals c.ID into merged
                from n in merged.DefaultIfEmpty(new HardwareConfig() { ID = acc.ID, Name = acc.Name, ActualName = acc.Name })
                where n.Enabled
                orderby n.Order descending, n.Name ascending
                select new ClaudeMonitor(acc.ID, string.IsNullOrEmpty(n.Name) ? acc.Name : n.Name, acc.CredentialsPath, metrics, _showName)
                ).ToArray();
        }

        public override void Update()
        {
            if (!_busy && DateTime.UtcNow >= _nextFetch)
            {
                _busy = true;

                Refresh();
            }

            ShowReset();
        }

        private async void Refresh()
        {
            ClaudeUsage _usage = await ClaudeUsageClient.FetchAsync(_credentialsPath).ConfigureAwait(false);

            Application _app = Application.Current;

            if (_app == null)
            {
                return;
            }

            _app.Dispatcher.BeginInvoke(new Action(() =>
            {
                _busy = false;
                _nextFetch = DateTime.UtcNow + (_usage.Status == ClaudeUsageStatus.RateLimited ? BACKOFF : REFRESH);

                Apply(_usage);
            }));
        }

        private void Apply(ClaudeUsage usage)
        {
            _status = usage.Status;

            if (usage.Status != ClaudeUsageStatus.Ok)
            {
                // Se conservan los ultimos valores buenos; el aviso va en la linea de reinicio
                // (o en el primer metric si no esta activa), resaltado como alerta.
                ClaudeMetric _target = _resetMetric ?? _session ?? _week;

                if (_target != null)
                {
                    _target.SetText(StatusText(usage.Status));
                    _target.SetAlert(true);
                }

                return;
            }

            foreach (ClaudeMetric _metric in new ClaudeMetric[] { _session, _week, _resetMetric })
            {
                if (_metric != null)
                {
                    _metric.SetAlert(false);
                }
            }

            _hasData = true;

            _sessionReset = usage.SessionReset;

            if (_session != null && usage.SessionPercent.HasValue)
            {
                _session.SetPercent(usage.SessionPercent.Value);
            }

            if (_week != null && usage.WeekPercent.HasValue)
            {
                _week.SetPercent(usage.WeekPercent.Value);
            }

            ShowReset();
        }

        private void ShowReset()
        {
            if (_resetMetric == null || _status != ClaudeUsageStatus.Ok || !_hasData)
            {
                return;
            }

            if (!_sessionReset.HasValue)
            {
                _resetMetric.SetText("-");
                return;
            }

            TimeSpan _left = _sessionReset.Value - DateTime.UtcNow;

            if (_left <= TimeSpan.Zero)
            {
                _resetMetric.SetText("0m");
            }
            else if (_left.TotalHours >= 1)
            {
                _resetMetric.SetText(string.Format("{0}h {1:00}m", (int)_left.TotalHours, _left.Minutes));
            }
            else
            {
                _resetMetric.SetText(string.Format("{0}m", Math.Max(1, _left.Minutes)));
            }
        }

        private static string StatusText(ClaudeUsageStatus status)
        {
            switch (status)
            {
                case ClaudeUsageStatus.NoCredentials:
                    return "sin credenciales";

                case ClaudeUsageStatus.TokenExpired:
                    return "token vencido: abre claude";

                case ClaudeUsageStatus.SessionExpired:
                    return "sesion vencida: /login";

                case ClaudeUsageStatus.RateLimited:
                    return "limitado (429)";

                default:
                    return "sin conexion";
            }
        }

        private string _credentialsPath { get; set; }

        private ClaudeMetric _session { get; set; }

        private ClaudeMetric _week { get; set; }

        private ClaudeMetric _resetMetric { get; set; }

        private DateTime? _sessionReset { get; set; }

        private ClaudeUsageStatus _status { get; set; } = ClaudeUsageStatus.Ok;

        private bool _hasData { get; set; } = false;

        private bool _busy { get; set; } = false;

        private DateTime _nextFetch { get; set; } = DateTime.MinValue;
    }
}
