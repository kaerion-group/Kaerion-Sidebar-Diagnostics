using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SidebarDiagnostics.Monitoring
{
    /// <summary>
    /// Una cuenta de Claude. Puede venir de claude-swap (SwapEmail) o apuntar al .credentials.json
    /// de su carpeta de configuracion (CLAUDE_CONFIG_DIR). Los tokens nunca se copian: se leen
    /// del archivo en cada consulta.
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

        // Solo para cuentas detectadas en claude-swap; no se guardan.
        public string SwapEmail { get; set; }

        public bool IsSwap
        {
            get { return !string.IsNullOrEmpty(SwapEmail); }
        }
    }

    public static class ClaudeAccountStore
    {
        public const string DEFAULTID = "default";

        public const string SWAPPREFIX = "swap:";

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

        /// <summary>
        /// Cuentas de claude-swap (si esta instalado), mas las guardadas a mano y la cuenta por defecto
        /// de este equipo que no esten ya en claude-swap.
        /// </summary>
        public static ClaudeAccount[] Load()
        {
            List<ClaudeAccount> _accounts = ClaudeSwap.List(ClaudeSwap.MAXAGE).Select(s => new ClaudeAccount()
            {
                ID = SWAPPREFIX + s.Email,
                Name = string.IsNullOrEmpty(s.Alias) ? s.Email : s.Alias,
                SwapEmail = s.Email
            }).ToList();

            List<ClaudeAccount> _local = new List<ClaudeAccount>();

            lock (_lock)
            {
                _local.AddRange(LoadSaved().Where(a => !string.IsNullOrEmpty(a.ID)));
            }

            if (_local.All(a => a.ID != DEFAULTID) && File.Exists(DefaultCredentialsPath))
            {
                _local.Insert(0, new ClaudeAccount() { ID = DEFAULTID, Name = GetEmail(DefaultCredentialsPath) ?? "Claude", CredentialsPath = DefaultCredentialsPath });
            }

            // Una cuenta que ya gestiona claude-swap no se repite.
            foreach (ClaudeAccount _account in _local)
            {
                string _email = GetEmail(_account.CredentialsPath);

                if (_email == null || _accounts.All(a => !string.Equals(a.SwapEmail, _email, StringComparison.OrdinalIgnoreCase)))
                {
                    _accounts.Add(_account);
                }
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
                if (string.IsNullOrEmpty(credentialsPath))
                {
                    return null;
                }

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

        /// <summary>Texto para la columna de estado en Ajustes.</summary>
        public static string Describe(ClaudeAccount account)
        {
            if (account.IsSwap)
            {
                ClaudeSwapAccount _swap = ClaudeSwap.List(ClaudeSwap.MAXAGE).FirstOrDefault(s => string.Equals(s.Email, account.SwapEmail, StringComparison.OrdinalIgnoreCase));

                if (_swap == null)
                {
                    return "claude-swap: cuenta no encontrada";
                }

                List<string> _parts = new List<string>() { string.Format("claude-swap #{0}", _swap.Number) };

                if (_swap.Active)
                {
                    _parts.Add("activa");
                }

                if (_swap.Usage.Status != ClaudeUsageStatus.Ok)
                {
                    _parts.Add("sin datos de uso");
                }
                else if (_swap.FetchedAt.HasValue)
                {
                    _parts.Add("datos de hace " + ClaudeMetric.FormatSpan(DateTime.UtcNow - _swap.FetchedAt.Value));
                }

                return string.Join(" - ", _parts);
            }

            string _email = GetEmail(account.CredentialsPath);
            string _status = DescribeStatus(account.CredentialsPath);

            return string.IsNullOrEmpty(_email) ? _status : string.Format("{0} - {1}", _email, _status);
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
            catch (Exception)
            {
                // archivo corrupto: se ignora y se sigue con las demas cuentas
            }

            return new List<ClaudeAccount>();
        }

        private static void Save(List<ClaudeAccount> accounts)
        {
            Directory.CreateDirectory(Utilities.Paths.LocalApp);

            File.WriteAllText(StoreFile, JsonConvert.SerializeObject(accounts, Formatting.Indented));
        }
    }

    public class ClaudeSwapAccount
    {
        public int Number { get; set; }

        public string Email { get; set; }

        public string Alias { get; set; }

        public bool Active { get; set; }

        public DateTime? FetchedAt { get; set; }

        public ClaudeUsage Usage { get; set; }
    }

    /// <summary>
    /// Lee las cuentas de claude-swap con "claude-swap list --json" (salida documentada para scripts).
    /// Solo devuelve correos y uso ya calculado: la app no toca sus credenciales cifradas.
    /// </summary>
    public static class ClaudeSwap
    {
        public static readonly TimeSpan MAXAGE = TimeSpan.FromSeconds(90);

        private static readonly TimeSpan TIMEOUT = TimeSpan.FromSeconds(20);

        private static readonly object _lock = new object();

        private static ClaudeSwapAccount[] _cache = new ClaudeSwapAccount[0];

        private static DateTime _cachedAt = DateTime.MinValue;

        private static bool _lastOk = false;

        public static string ExePath
        {
            get
            {
                string _local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude-swap.exe");

                if (File.Exists(_local))
                {
                    return _local;
                }

                foreach (string _dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                {
                    try
                    {
                        string _candidate = Path.Combine(_dir.Trim(), "claude-swap.exe");

                        if (File.Exists(_candidate))
                        {
                            return _candidate;
                        }
                    }
                    catch (ArgumentException) { }
                }

                return null;
            }
        }

        /// <summary>True si la ultima lectura de claude-swap funciono.</summary>
        public static bool LastOk
        {
            get { lock (_lock) { return _lastOk; } }
        }

        /// <summary>Cuentas de claude-swap; reutiliza la ultima lectura si tiene menos de maxAge.</summary>
        public static ClaudeSwapAccount[] List(TimeSpan maxAge)
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _cachedAt < maxAge)
                {
                    return _cache;
                }

                _cachedAt = DateTime.UtcNow;

                string _json = Run("list --json");

                _lastOk = _json != null;

                if (_lastOk)
                {
                    try
                    {
                        _cache = Parse(JObject.Parse(_json));
                    }
                    catch (Exception)
                    {
                        _lastOk = false;
                    }
                }

                return _cache;
            }
        }

        private static string Run(string arguments)
        {
            string _exe = ExePath;

            if (_exe == null)
            {
                return null;
            }

            try
            {
                using (Process _process = new Process())
                {
                    _process.StartInfo = new ProcessStartInfo(_exe, arguments)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8
                    };

                    _process.Start();

                    Task<string> _output = _process.StandardOutput.ReadToEndAsync();
                    Task<string> _errors = _process.StandardError.ReadToEndAsync();

                    if (!_process.WaitForExit((int)TIMEOUT.TotalMilliseconds))
                    {
                        try { _process.Kill(); } catch (Exception) { }

                        return null;
                    }

                    return _process.ExitCode == 0 ? _output.Result : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static ClaudeSwapAccount[] Parse(JObject json)
        {
            JArray _accounts = json["accounts"] as JArray;

            if (_accounts == null)
            {
                return new ClaudeSwapAccount[0];
            }

            return _accounts.OfType<JObject>().Where(a => !string.IsNullOrEmpty((string)a["email"])).Select(a =>
            {
                JObject _usage = a["usage"] as JObject;

                ClaudeUsage _result = new ClaudeUsage();

                if ((string)a["usageStatus"] != "ok" || _usage == null)
                {
                    _result.Status = ClaudeUsageStatus.Unavailable;
                }
                else
                {
                    _result.SessionPercent = (double?)_usage["fiveHour"]?["pct"];
                    _result.SessionReset = ClaudeUsageClient.ParseDate(_usage["fiveHour"]?["resetsAt"]);
                    _result.WeekPercent = (double?)_usage["sevenDay"]?["pct"];
                    _result.WeekReset = ClaudeUsageClient.ParseDate(_usage["sevenDay"]?["resetsAt"]);
                }

                return new ClaudeSwapAccount()
                {
                    Number = (int?)a["number"] ?? 0,
                    Email = (string)a["email"],
                    Alias = (string)a["alias"],
                    Active = (bool?)a["active"] ?? false,
                    FetchedAt = ClaudeUsageClient.ParseDate(a["usageFetchedAt"]),
                    Usage = _result
                };
            }).OrderBy(a => a.Number).ToArray();
        }
    }

    public enum ClaudeUsageStatus : byte
    {
        Ok,
        NoCredentials,
        TokenExpired,
        SessionExpired,
        RateLimited,
        Unavailable,
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

        /// <summary>
        /// Cuentas de claude-swap: la activa se consulta directo con ~/.claude (dato al minuto) y las
        /// demas salen de la cache de claude-swap. El resto de cuentas, directo con su .credentials.json.
        /// </summary>
        public static async Task<ClaudeUsage> FetchAsync(ClaudeAccount account)
        {
            if (!account.IsSwap)
            {
                return await FetchAsync(account.CredentialsPath).ConfigureAwait(false);
            }

            ClaudeSwapAccount _swap = (await Task.Run(() => ClaudeSwap.List(ClaudeSwap.MAXAGE)).ConfigureAwait(false))
                .FirstOrDefault(s => string.Equals(s.Email, account.SwapEmail, StringComparison.OrdinalIgnoreCase));

            if (_swap == null)
            {
                return new ClaudeUsage() { Status = ClaudeSwap.LastOk ? ClaudeUsageStatus.NoCredentials : ClaudeUsageStatus.Error };
            }

            string _default = ClaudeAccountStore.DefaultCredentialsPath;

            if (_swap.Active && string.Equals(ClaudeAccountStore.GetEmail(_default), _swap.Email, StringComparison.OrdinalIgnoreCase))
            {
                ClaudeUsage _direct = await FetchAsync(_default).ConfigureAwait(false);

                if (_direct.Status == ClaudeUsageStatus.Ok || _swap.Usage.Status != ClaudeUsageStatus.Ok)
                {
                    return _direct;
                }
            }

            return _swap.Usage;
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

        public static DateTime? ParseDate(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            // Newtonsoft ya convierte las fechas ISO al leer el JSON.
            if (token.Type == JTokenType.Date)
            {
                object _raw = ((JValue)token).Value;

                return _raw is DateTimeOffset ? ((DateTimeOffset)_raw).UtcDateTime : ((DateTime)_raw).ToUniversalTime();
            }

            DateTimeOffset _date;

            if (DateTimeOffset.TryParse((string)token, out _date))
            {
                return _date.UtcDateTime;
            }

            return null;
        }
    }

    /// <summary>Porcentaje de una ventana de uso con la cuenta atras de su reinicio en la misma linea.</summary>
    public class ClaudeMetric : BaseMetric
    {
        public ClaudeMetric(MetricKey key) : base(key, DataType.Percent)
        {
            Text = "...";
        }

        public void SetUsage(double? percent, DateTime? reset)
        {
            if (percent.HasValue)
            {
                _percent = Math.Round(percent.Value);
                Value = nValue = _percent.Value;
            }

            _reset = reset;
            _showStatus = false;

            Refresh();
        }

        public void SetStatus(string text)
        {
            _showStatus = true;

            Text = text;
        }

        public void SetAlert(bool alert)
        {
            if (IsAlert != alert)
            {
                IsAlert = alert;
            }
        }

        /// <summary>Recalcula la cuenta atras; se llama en cada actualizacion del sidebar.</summary>
        public void Refresh()
        {
            if (_showStatus || !_percent.HasValue)
            {
                return;
            }

            string _text = string.Format("{0}%", _percent.Value);

            if (_reset.HasValue)
            {
                _text += " \u00B7 " + FormatSpan(_reset.Value - DateTime.UtcNow);
            }

            if (Text != _text)
            {
                Text = _text;
            }
        }

        public static string FormatSpan(TimeSpan span)
        {
            if (span <= TimeSpan.Zero)
            {
                return "0m";
            }

            if (span.TotalDays >= 1)
            {
                return string.Format("{0}d {1}h", (int)span.TotalDays, span.Hours);
            }

            if (span.TotalHours >= 1)
            {
                return string.Format("{0}h {1:00}m", (int)span.TotalHours, span.Minutes);
            }

            return string.Format("{0}m", Math.Max(1, span.Minutes));
        }

        private double? _percent { get; set; }

        private DateTime? _reset { get; set; }

        private bool _showStatus { get; set; } = false;
    }

    public class ClaudeMonitor : BaseMonitor
    {
        private static readonly TimeSpan REFRESH = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan BACKOFF = TimeSpan.FromMinutes(5);

        public ClaudeMonitor(ClaudeAccount account, string alias, MetricConfig[] metrics, bool showName = true) : base(account.ID, string.IsNullOrEmpty(alias) ? account.Name : alias, showName)
        {
            _account = account;

            DefaultName = account.Name;
            Alias = alias;

            List<iMetric> _metrics = new List<iMetric>();

            if (metrics.IsEnabled(MetricKey.ClaudeSession))
            {
                _session = new ClaudeMetric(MetricKey.ClaudeSession);
                _metrics.Add(_session);
            }

            if (metrics.IsEnabled(MetricKey.ClaudeWeek))
            {
                _week = new ClaudeMetric(MetricKey.ClaudeWeek);
                _metrics.Add(_week);
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
                Info = ClaudeAccountStore.Describe(a)
            });
        }

        public static iMonitor[] GetInstances(HardwareConfig[] hardwareConfig, MetricConfig[] metrics, ConfigParam[] parameters)
        {
            bool _showName = parameters.GetValue<bool>(ParamKey.HardwareNames);

            ClaudeAccount[] _accounts = ClaudeAccountStore.Load();

            return (
                from acc in _accounts
                join c in hardwareConfig on acc.ID equals c.ID into merged
                from n in merged.DefaultIfEmpty(new HardwareConfig() { ID = acc.ID, Name = null, ActualName = acc.Name })
                where n.Enabled
                orderby n.Order descending, (string.IsNullOrEmpty(n.Name) ? acc.Name : n.Name) ascending
                select new ClaudeMonitor(acc, string.Equals(n.Name, acc.Name, StringComparison.Ordinal) ? null : n.Name, metrics, _showName)
                ).ToArray();
        }

        /// <summary>Correo (o alias de claude-swap) de la cuenta, usado cuando no hay pseudonimo.</summary>
        public string DefaultName { get; private set; }

        /// <summary>Pseudonimo puesto en el sidebar o en Ajustes; null si no hay.</summary>
        public string Alias { get; private set; }

        /// <summary>Cambia el pseudonimo y lo guarda en la configuracion del monitor (nombre del hardware).</summary>
        public void SetAlias(string alias)
        {
            alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();

            MonitorConfig _config = Framework.Settings.Instance.MonitorConfig?.FirstOrDefault(c => c.Type == MonitorType.Claude);

            if (_config != null)
            {
                List<HardwareConfig> _hardware = (_config.Hardware ?? new HardwareConfig[0]).ToList();

                HardwareConfig _record = _hardware.FirstOrDefault(h => h.ID == ID);

                if (_record == null)
                {
                    _record = new HardwareConfig() { ID = ID, Enabled = true };
                    _hardware.Add(_record);
                }

                _record.Name = alias;

                _config.Hardware = _hardware.ToArray();

                Framework.Settings.Instance.Save();
            }

            Alias = alias;
            Name = alias ?? DefaultName;
        }

        public override void Update()
        {
            if (!_busy && DateTime.UtcNow >= _nextFetch)
            {
                _busy = true;

                Fetch();
            }

            _session?.Refresh();
            _week?.Refresh();
        }

        private async void Fetch()
        {
            ClaudeUsage _usage = await ClaudeUsageClient.FetchAsync(_account).ConfigureAwait(false);

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
            if (usage.Status != ClaudeUsageStatus.Ok)
            {
                // Se conservan los ultimos valores buenos de la semana; el aviso va en la primera
                // linea, resaltado como alerta.
                ClaudeMetric _target = _session ?? _week;

                if (_target != null)
                {
                    _target.SetStatus(StatusText(usage.Status));
                    _target.SetAlert(true);
                }

                return;
            }

            if (_session != null)
            {
                _session.SetAlert(false);
                _session.SetUsage(usage.SessionPercent, usage.SessionReset);
            }

            if (_week != null)
            {
                _week.SetAlert(false);
                _week.SetUsage(usage.WeekPercent, usage.WeekReset);
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

                case ClaudeUsageStatus.Unavailable:
                    return "sin datos";

                default:
                    return "sin conexion";
            }
        }

        private ClaudeAccount _account { get; set; }

        private ClaudeMetric _session { get; set; }

        private ClaudeMetric _week { get; set; }

        private bool _busy { get; set; } = false;

        private DateTime _nextFetch { get; set; } = DateTime.MinValue;
    }

    /// <summary>Ventana minima para escribir el pseudonimo de una cuenta.</summary>
    public static class ClaudeAliasPrompt
    {
        /// <summary>Devuelve el texto escrito ("" para quitar el pseudonimo) o null si se cancela.</summary>
        public static string Show(Window owner, string current, string defaultName)
        {
            TextBox _input = new TextBox() { Text = current ?? "", MinWidth = 280, Margin = new Thickness(0, 6, 0, 12), Padding = new Thickness(3) };

            Button _ok = new Button() { Content = "Aceptar", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
            Button _cancel = new Button() { Content = "Cancelar", IsCancel = true, MinWidth = 80, Padding = new Thickness(8, 3, 8, 3) };

            StackPanel _buttons = new StackPanel() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _buttons.Children.Add(_ok);
            _buttons.Children.Add(_cancel);

            StackPanel _panel = new StackPanel() { Margin = new Thickness(14) };
            _panel.Children.Add(new TextBlock() { Text = string.Format("Pseudonimo para {0}\n(vacio para mostrar el correo)", defaultName) });
            _panel.Children.Add(_input);
            _panel.Children.Add(_buttons);

            Window _window = new Window()
            {
                Title = "Pseudonimo",
                Content = _panel,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                Topmost = true
            };

            if (owner != null && owner.IsVisible)
            {
                _window.Owner = owner;
            }

            _ok.Click += (s, e) => { _window.DialogResult = true; };

            _window.Loaded += (s, e) =>
            {
                _input.Focus();
                _input.SelectAll();
            };

            return _window.ShowDialog() == true ? _input.Text : null;
        }
    }
}
