using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
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
                    _parts.Add(_swap.TokenExpired
                        ? string.Format("token vencido: ejecuta claude-swap run {0} para renovarlo", _swap.Number)
                        : "sin datos de uso");
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

        public bool TokenExpired { get; set; }

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

                string _errors;
                string _json = Run("list --json", out _errors);

                _lastOk = _json != null;

                if (_lastOk)
                {
                    try
                    {
                        _cache = Parse(JObject.Parse(_json));

                        if (_cache.Any(a => a.Usage.Status == ClaudeUsageStatus.Unavailable))
                        {
                            MarkExpiredTokens(_cache);
                        }
                    }
                    catch (Exception)
                    {
                        _lastOk = false;
                    }
                }

                return _cache;
            }
        }

        /// <summary>Descarta la ultima lectura para que la proxima consulta vuelva a llamar a claude-swap.</summary>
        public static void Invalidate()
        {
            lock (_lock)
            {
                _cachedAt = DateTime.MinValue;
            }
        }

        /// <summary>Activa otra cuenta con "claude-swap switch"; devuelve el error o null si funciono.</summary>
        public static string Switch(string email)
        {
            if (ExePath == null)
            {
                return "No se encontro claude-swap.";
            }

            string _errors;
            string _output = Run("switch " + email, out _errors);

            Invalidate();

            return _output != null ? null : (string.IsNullOrWhiteSpace(_errors) ? "claude-swap no pudo cambiar de cuenta." : _errors.Trim());
        }

        // "--token-status" no se puede combinar con "--json": se lee la salida de texto, donde cada
        // cuenta empieza con "N: correo" y su token aparece en una linea "oauth: expired".
        private static void MarkExpiredTokens(ClaudeSwapAccount[] accounts)
        {
            string _errors;
            string _text = Run("list --token-status", out _errors);

            if (_text == null)
            {
                return;
            }

            ClaudeSwapAccount _current = null;

            foreach (string _line in _text.Split('\n'))
            {
                Match _account = Regex.Match(_line, @"^\s*(\d+):\s+(\S+@\S+)");

                if (_account.Success)
                {
                    _current = accounts.FirstOrDefault(a => string.Equals(a.Email, _account.Groups[2].Value, StringComparison.OrdinalIgnoreCase));
                    continue;
                }

                if (_current != null && Regex.IsMatch(_line, @"oauth:\s*expired", RegexOptions.IgnoreCase))
                {
                    _current.TokenExpired = true;

                    if (_current.Usage.Status == ClaudeUsageStatus.Unavailable)
                    {
                        _current.Usage.Status = ClaudeUsageStatus.TokenExpired;
                    }
                }
            }
        }

        private static string Run(string arguments, out string errors)
        {
            errors = null;

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

                    errors = _errors.Result;

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

                ClaudeUsage _result = new ClaudeUsage()
                {
                    FetchedAt = ClaudeUsageClient.ParseDate(a["usageFetchedAt"]),
                    Active = (bool?)a["active"] ?? false,
                    SwapNumber = (int?)a["number"] ?? 0
                };

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

        /// <summary>Momento en que se obtuvo el dato (consulta directa o cache de claude-swap).</summary>
        public DateTime? FetchedAt { get; set; }

        /// <summary>Cuenta activa en claude-swap.</summary>
        public bool Active { get; set; }

        /// <summary>Numero de la cuenta en claude-swap; 0 si no viene de ahi.</summary>
        public int SwapNumber { get; set; }
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

        // claude-swap refresca su cache cada ~5 min; pasado este margen el dato se considera viejo.
        private static readonly TimeSpan STALE = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Cuentas de claude-swap: salen de su cache; si el dato esta viejo, la activa se consulta
        /// directo con ~/.claude. El resto de cuentas, directo con su .credentials.json.
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

            // Si el dato de claude-swap es reciente se usa tal cual: consultar directo gasta el mismo
            // cupo de consultas por token que claude-swap y provoca 429.
            if (_swap.Usage.Status == ClaudeUsageStatus.Ok && _swap.FetchedAt.HasValue && DateTime.UtcNow - _swap.FetchedAt.Value < STALE)
            {
                return _swap.Usage;
            }

            string _default = ClaudeAccountStore.DefaultCredentialsPath;

            // Dato viejo: solo la cuenta activa se puede consultar directo, con ~/.claude.
            if (_swap.Active && string.Equals(ClaudeAccountStore.GetEmail(_default), _swap.Email, StringComparison.OrdinalIgnoreCase))
            {
                ClaudeUsage _direct = await FetchAsync(_default).ConfigureAwait(false);

                _direct.Active = true;
                _direct.SwapNumber = _swap.Number;

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
            _usage.FetchedAt = DateTime.UtcNow;

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

    /// <summary>
    /// Peso de una cuenta segun el plan escrito entre parentesis en su nombre:
    /// "(Pro)" = 1, "(5X)", "(X5)" o "(Max5)" = 5, "(20X)" = 20, "(Max)" = 5. Sin etiqueta cuenta como Pro.
    /// </summary>
    public static class ClaudePlan
    {
        private static readonly Regex _tag = new Regex(@"\((?:pro|(\d+)\s*x|(?:max\s*)?x\s*(\d+)|max\s*(\d+)?)\)", RegexOptions.IgnoreCase);

        public static double Weight(string name)
        {
            Match _match = _tag.Match(name ?? "");

            if (!_match.Success)
            {
                return 1;
            }

            for (int i = 1; i <= 3; i++)
            {
                if (_match.Groups[i].Success)
                {
                    return Math.Max(1, int.Parse(_match.Groups[i].Value));
                }
            }

            return _match.Value.IndexOf("max", StringComparison.OrdinalIgnoreCase) >= 0 ? 5 : 1;
        }

        /// <summary>La etiqueta de plan del nombre, como " (Pro)", o "" si no tiene.</summary>
        public static string Tag(string name)
        {
            Match _match = _tag.Match(name ?? "");

            return _match.Success ? " " + _match.Value : "";
        }
    }

    /// <summary>
    /// Porcentaje de una ventana de uso con la cuenta atras de su reinicio en la misma linea y, si al
    /// ritmo actual se agota antes del reinicio, el tiempo que le queda.
    /// </summary>
    public class ClaudeMetric : BaseMetric
    {
        public const byte LEVELNORMAL = 0;
        public const byte LEVELWARN = 1;
        public const byte LEVELCRITICAL = 2;

        private const double WARN = 75;
        private const double CRITICAL = 90;

        private static readonly TimeSpan MINSPAN = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan MAXLOOKBACK = TimeSpan.FromHours(6);

        public ClaudeMetric(MetricKey key, TimeSpan window, string label = null) : base(key, DataType.Percent, label)
        {
            _window = window;

            Text = "...";
        }

        public void SetUsage(double? percent, DateTime? reset, DateTime? fetchedAt)
        {
            if (percent.HasValue)
            {
                _percent = Math.Round(percent.Value);
                Value = nValue = _percent.Value;

                AddSample(fetchedAt ?? DateTime.UtcNow, percent.Value);
            }

            _reset = reset;
            _fixedText = false;

            Refresh();
        }

        /// <summary>Texto fijo (un aviso o el total), sin cuenta atras.</summary>
        public void SetText(string text, byte level = LEVELNORMAL)
        {
            _fixedText = true;

            if (Text != text)
            {
                Text = text;
            }

            Level = level;
        }

        public void SetAlert(bool alert)
        {
            if (IsAlert != alert)
            {
                IsAlert = alert;
            }
        }

        /// <summary>Recalcula la cuenta atras y la proyeccion; se llama en cada actualizacion del sidebar.</summary>
        public void Refresh()
        {
            if (_fixedText || !_percent.HasValue)
            {
                return;
            }

            DateTime _now = DateTime.UtcNow;

            string _text = _percent.Value >= 100 ? "agotada" : string.Format("{0}%", _percent.Value);

            TimeSpan? _exhaust = null;

            if (_reset.HasValue)
            {
                _text += " \u00B7 " + FormatSpan(_reset.Value - _now);

                _exhaust = Projection(_now);
            }

            Exhaust = _exhaust;

            if (Text != _text)
            {
                Text = _text;
            }

            // La proyeccion se muestra aparte, en la fila de aviso; aqui solo cuenta el consumo.
            Level = _percent.Value >= CRITICAL ? LEVELCRITICAL : (_percent.Value >= WARN ? LEVELWARN : LEVELNORMAL);
        }

        /// <summary>Tiempo hasta agotar la ventana al ritmo actual, si ocurre antes del reinicio; null si no.</summary>
        public TimeSpan? Exhaust { get; private set; }

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

        private void AddSample(DateTime time, double percent)
        {
            if (_samples.Count > 0 && _samples[_samples.Count - 1].Key >= time)
            {
                return;
            }

            _samples.Add(new KeyValuePair<DateTime, double>(time, percent));

            _samples.RemoveAll(s => s.Key < time - _window);
        }

        // Tiempo hasta el 100 % al ritmo actual, solo si llega antes del reinicio. El ritmo sale de las
        // lecturas recientes (la ultima hora en la ventana de 5h, las ultimas 6h en la semanal); si aun
        // no cubren 20 min, del promedio desde que empezo la ventana.
        private TimeSpan? Projection(DateTime now)
        {
            if (!_reset.HasValue || !_percent.HasValue || _percent.Value >= 100 || _reset.Value <= now)
            {
                return null;
            }

            double? _rate = Rate(now);

            if (!_rate.HasValue || _rate.Value <= 0)
            {
                return null;
            }

            TimeSpan _toFull = TimeSpan.FromHours((100 - _percent.Value) / _rate.Value);

            return now + _toFull < _reset.Value ? _toFull : (TimeSpan?)null;
        }

        /// <summary>Ritmo de consumo actual en puntos de porcentaje por hora; null sin datos suficientes.</summary>
        public double? Rate(DateTime now)
        {
            if (!_reset.HasValue || !_percent.HasValue)
            {
                return null;
            }

            // La ventana ya se reinicio desde la ultima lectura: aun no hay ritmo nuevo.
            if (_reset.Value <= now)
            {
                return 0;
            }

            DateTime _start = _reset.Value - _window;
            DateTime _since = now - TimeSpan.FromTicks(Math.Min(_window.Ticks / 5, MAXLOOKBACK.Ticks));

            KeyValuePair<DateTime, double>[] _recent = _samples.Where(s => s.Key > _start && s.Key >= _since).ToArray();

            double? _rate = null;

            if (_recent.Length >= 2)
            {
                KeyValuePair<DateTime, double> _first = _recent[0];
                KeyValuePair<DateTime, double> _last = _recent[_recent.Length - 1];

                if (_last.Key - _first.Key >= MINSPAN && _last.Value >= _first.Value)
                {
                    _rate = (_last.Value - _first.Value) / (_last.Key - _first.Key).TotalHours;
                }
            }

            if (!_rate.HasValue && now - _start >= MINSPAN)
            {
                _rate = _percent.Value / (now - _start).TotalHours;
            }

            return _rate;
        }

        private byte _level { get; set; } = LEVELNORMAL;

        /// <summary>0 normal, 1 aviso (75 % o se agota antes del reinicio), 2 critico (90 %).</summary>
        public byte Level
        {
            get
            {
                return _level;
            }
            private set
            {
                if (_level != value)
                {
                    _level = value;

                    NotifyPropertyChanged("Level");
                }
            }
        }

        private TimeSpan _window { get; set; }

        private double? _percent { get; set; }

        private DateTime? _reset { get; set; }

        private bool _fixedText { get; set; } = false;

        private List<KeyValuePair<DateTime, double>> _samples { get; set; } = new List<KeyValuePair<DateTime, double>>();
    }

    /// <summary>Base comun de las filas de Claude en el sidebar (cuentas y total).</summary>
    public abstract class ClaudeBaseMonitor : BaseMonitor
    {
        protected static readonly TimeSpan SESSION = TimeSpan.FromHours(5);
        protected static readonly TimeSpan WEEK = TimeSpan.FromDays(7);

        protected ClaudeBaseMonitor(string id, string name, bool showName) : base(id, name, showName) { }

        public virtual bool IsAccount
        {
            get { return false; }
        }

        public virtual bool CanSwitch
        {
            get { return false; }
        }

        private bool _isStale { get; set; } = false;

        /// <summary>El dato mostrado tiene mas de 10 minutos.</summary>
        public bool IsStale
        {
            get
            {
                return _isStale;
            }
            protected set
            {
                if (_isStale != value)
                {
                    _isStale = value;

                    NotifyPropertyChanged("IsStale");
                }
            }
        }
    }

    public class ClaudeMonitor : ClaudeBaseMonitor
    {
        private static readonly TimeSpan REFRESH = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan BACKOFF = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan STALE = TimeSpan.FromMinutes(10);

        // Sobra a partir de la cual la fila se pone en amarillo.
        public const double LEFTOVERWARN = 20;

        private static DateTime _refreshRequested = DateTime.MinValue;

        public ClaudeMonitor(ClaudeAccount account, string alias, MetricConfig[] metrics, bool showName = true) : base(account.ID, string.IsNullOrEmpty(alias) ? account.Name : alias, showName)
        {
            _account = account;

            DefaultName = account.Name;
            Alias = alias;

            List<iMetric> _metrics = new List<iMetric>();

            if (metrics.IsEnabled(MetricKey.ClaudeSession))
            {
                _session = new ClaudeMetric(MetricKey.ClaudeSession, SESSION);
                _metrics.Add(_session);
            }

            if (metrics.IsEnabled(MetricKey.ClaudeWeek))
            {
                _week = new ClaudeMetric(MetricKey.ClaudeWeek, WEEK);
                _metrics.Add(_week);
            }

            if (metrics.IsEnabled(MetricKey.ClaudeAdvice))
            {
                // Sin etiqueta: la fila solo aparece cuando hay algo que hacer.
                _advice = new ClaudeMetric(MetricKey.ClaudeAdvice, WEEK, "");
                _metrics.Add(_advice);
            }

            Metrics = _metrics.ToArray();

            _instances.Add(new WeakReference<ClaudeMonitor>(this));
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

            ClaudeMonitor[] _monitors = (
                from acc in _accounts
                join c in hardwareConfig on acc.ID equals c.ID into merged
                from n in merged.DefaultIfEmpty(new HardwareConfig() { ID = acc.ID, Name = null, ActualName = acc.Name })
                where n.Enabled
                orderby n.Order descending, (string.IsNullOrEmpty(n.Name) ? acc.Name : n.Name) ascending
                select new ClaudeMonitor(acc, string.Equals(n.Name, acc.Name, StringComparison.Ordinal) ? null : n.Name, metrics, _showName)
                ).ToArray();

            if (_monitors.Length < 2)
            {
                return _monitors;
            }

            return new iMonitor[] { new ClaudeTotalMonitor(_monitors, metrics, parameters, _showName) }.Concat(_monitors).ToArray();
        }

        /// <summary>Hace que todas las cuentas vuelvan a consultar en la proxima actualizacion.</summary>
        public static void RequestRefresh()
        {
            _refreshRequested = DateTime.UtcNow;
        }

        /// <summary>
        /// Activa la cuenta con claude-swap y, si funciona, la marca como activa en el sidebar sin esperar
        /// a la siguiente consulta. Devuelve el error, o null si funciono. Se llama desde el hilo de la UI.
        /// </summary>
        public static async Task<string> SwitchTo(string email)
        {
            string _error = await Task.Run(() => ClaudeSwap.Switch(email));

            if (_error == null)
            {
                _instances.RemoveAll(r => { ClaudeMonitor _m; return !r.TryGetTarget(out _m); });

                foreach (WeakReference<ClaudeMonitor> _reference in _instances)
                {
                    ClaudeMonitor _monitor;

                    if (_reference.TryGetTarget(out _monitor) && _monitor._account.IsSwap)
                    {
                        _monitor.IsActive = string.Equals(_monitor.SwapEmail, email, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }

            RequestRefresh();

            return _error;
        }

        // Monitores vivos, para reflejar un cambio de cuenta al instante; se recrean al recargar el sidebar.
        private static readonly List<WeakReference<ClaudeMonitor>> _instances = new List<WeakReference<ClaudeMonitor>>();

        /// <summary>Correo de la cuenta (el de claude-swap o el de su carpeta de configuracion).</summary>
        public string Email
        {
            get { return _account.SwapEmail ?? ClaudeAccountStore.GetEmail(_account.CredentialsPath) ?? DefaultName; }
        }

        /// <summary>Muestra el correo en lugar del pseudonimo, conservando la etiqueta del plan.</summary>
        public void ShowEmail()
        {
            SetAlias(Email + ClaudePlan.Tag(BaseName));
        }

        /// <summary>Porcentaje usado de una ventana segun el ultimo dato; 0 si ya se reinicio desde entonces.</summary>
        public double? UsedPercent(bool week)
        {
            if (LastUsage == null)
            {
                return null;
            }

            return 100 - Left(week ? LastUsage.WeekPercent : LastUsage.SessionPercent, week ? LastUsage.WeekReset : LastUsage.SessionReset, DateTime.UtcNow);
        }

        /// <summary>Proximo reinicio semanal; si el ultimo dato ya quedo atras, el de la semana siguiente.</summary>
        public DateTime? NextWeekReset
        {
            get
            {
                DateTime? _reset = LastUsage?.WeekReset;

                return _reset.HasValue && _reset.Value <= DateTime.UtcNow ? _reset.Value + WEEK : _reset;
            }
        }

        /// <summary>Horas hasta el proximo reinicio semanal; null sin datos.</summary>
        public double? HoursToWeekReset
        {
            get
            {
                DateTime? _reset = NextWeekReset;

                return _reset.HasValue ? Math.Max(0, (_reset.Value - DateTime.UtcNow).TotalHours) : (double?)null;
            }
        }

        /// <summary>
        /// Porcentaje semanal que quedaria sin usar al reiniciarse si se sigue al ritmo actual; null sin
        /// datos. Sin ritmo medido se asume que no se usa: todo lo libre se perderia.
        /// </summary>
        public double? LeftoverPercent
        {
            get
            {
                double? _used = UsedPercent(true);
                double? _hours = HoursToWeekReset;

                if (!_used.HasValue || !_hours.HasValue)
                {
                    return null;
                }

                double _rate = _week?.Rate(DateTime.UtcNow) ?? 0;

                return Math.Max(0, 100 - _used.Value - Math.Max(0, _rate) * _hours.Value);
            }
        }

        /// <summary>Lo que quedaria sin usar al reinicio, en unidades de plan (Pro = 1).</summary>
        public double? LeftoverUnits
        {
            get
            {
                double? _percent = LeftoverPercent;

                return _percent.HasValue ? _percent.Value * Weight / 100 : (double?)null;
            }
        }

        /// <summary>Puntos de porcentaje semanal por hora que hacen falta para llegar al 100 % justo al reinicio.</summary>
        public double? NeededPercentPerHour
        {
            get
            {
                double? _used = UsedPercent(true);
                double? _hours = HoursToWeekReset;

                if (!_used.HasValue || !_hours.HasValue)
                {
                    return null;
                }

                return (100 - _used.Value) / Math.Max(_hours.Value, 0.25);
            }
        }

        /// <summary>
        /// Capacidad que hay que gastar por hora, en unidades de plan, para no perder nada al reinicio. La
        /// cuenta con el valor mas alto es la que mas urge usar; no depende de cual se esta usando ahora.
        /// </summary>
        public double? NeededUnitsPerHour
        {
            get
            {
                double? _needed = NeededPercentPerHour;

                return _needed.HasValue ? _needed.Value * Weight / 100 : (double?)null;
            }
        }

        /// <summary>Consumo semanal actual en unidades de plan (Pro = 1) por hora; null sin datos.</summary>
        public double? WeekRate
        {
            get
            {
                double? _rate = _week?.Rate(DateTime.UtcNow);

                return _rate.HasValue ? _rate.Value * Weight / 100 : (double?)null;
            }
        }

        public override bool IsAccount
        {
            get { return true; }
        }

        public override bool CanSwitch
        {
            get { return _account.IsSwap && !IsActive; }
        }

        public string SwapEmail
        {
            get { return _account.SwapEmail; }
        }

        /// <summary>Correo (o alias de claude-swap) de la cuenta, usado cuando no hay pseudonimo.</summary>
        public string DefaultName { get; private set; }

        /// <summary>Pseudonimo puesto en el sidebar o en Ajustes; null si no hay.</summary>
        public string Alias { get; private set; }

        /// <summary>Nombre sin las marcas de activa o recomendada.</summary>
        public string BaseName
        {
            get { return Alias ?? DefaultName; }
        }

        /// <summary>Peso del plan de la cuenta (Pro = 1), segun la etiqueta de su nombre.</summary>
        public double Weight
        {
            get { return ClaudePlan.Weight(BaseName); }
        }

        /// <summary>Ultimo dato de uso valido; null si aun no hay.</summary>
        public ClaudeUsage LastUsage { get; private set; }

        public ClaudeUsageStatus Status { get; private set; } = ClaudeUsageStatus.Ok;

        private bool _isActive { get; set; } = false;

        public bool IsActive
        {
            get
            {
                return _isActive;
            }
            private set
            {
                if (_isActive != value)
                {
                    _isActive = value;

                    NotifyPropertyChanged("IsActive");
                    NotifyPropertyChanged("CanSwitch");

                    UpdateName();
                }
            }
        }

        private bool _isRecommended { get; set; } = false;

        /// <summary>La cuenta con mas capacidad libre ahora; lo decide la fila del total.</summary>
        public bool IsRecommended
        {
            get
            {
                return _isRecommended;
            }
            set
            {
                if (_isRecommended != value)
                {
                    _isRecommended = value;

                    NotifyPropertyChanged("IsRecommended");

                    UpdateName();
                }
            }
        }

        /// <summary>
        /// Capacidad libre en unidades de plan (Pro = 1); null sin datos. Con now = true se limita
        /// tambien por la ventana de 5 horas, que es lo que se puede usar en este momento.
        /// </summary>
        public double? Remaining(bool now)
        {
            if (LastUsage == null)
            {
                return null;
            }

            DateTime _utc = DateTime.UtcNow;

            double _left = Left(LastUsage.WeekPercent, LastUsage.WeekReset, _utc);

            if (now)
            {
                _left = Math.Min(_left, Left(LastUsage.SessionPercent, LastUsage.SessionReset, _utc));
            }

            return Weight * _left / 100;
        }

        // Si la ventana ya se reinicio desde la ultima lectura, vuelve a estar libre.
        private static double Left(double? percent, DateTime? reset, DateTime now)
        {
            if (reset.HasValue && reset.Value <= now)
            {
                return 100;
            }

            return Math.Max(0, 100 - (percent ?? 0));
        }

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

            UpdateName();
        }

        private void UpdateName()
        {
            string _prefix = IsActive ? "\u25CF " : (IsRecommended ? "\u2192 " : "");

            Name = _prefix + BaseName;
        }

        public override void Update()
        {
            if (!_busy && (DateTime.UtcNow >= _nextFetch || _refreshRequested > _fetchStarted))
            {
                _busy = true;
                _fetchStarted = DateTime.UtcNow;

                Fetch();
            }

            _session?.Refresh();
            _week?.Refresh();

            ShowAdvice();

            IsStale = LastUsage?.FetchedAt != null && DateTime.UtcNow - LastUsage.FetchedAt.Value > STALE;
        }

        // Fila de aviso, vacia (oculta) si no hay nada que hacer. Si al ritmo actual la ventana semanal se
        // agota antes del reinicio, cuanto falta; si sobraria el 20 % o mas, cuanto y el ritmo diario (o
        // por hora en el ultimo dia) que hace falta para gastarlo.
        private void ShowAdvice()
        {
            if (_advice == null)
            {
                return;
            }

            TimeSpan? _weekLeft = _week?.Exhaust;
            double? _leftover = LeftoverPercent;
            double? _needed = NeededPercentPerHour;
            double? _hours = HoursToWeekReset;

            if (LastUsage == null || Status != ClaudeUsageStatus.Ok)
            {
                _advice.SetText("");
            }
            else if (_weekLeft.HasValue)
            {
                _advice.SetText(string.Format("{0} se agota en {1}", (char)0x26A0, ClaudeMetric.FormatSpan(_weekLeft.Value)), ClaudeMetric.LEVELWARN);
            }
            else if (_leftover.HasValue && _leftover.Value >= LEFTOVERWARN && _needed.HasValue && _hours.HasValue)
            {
                bool _perDay = _hours.Value >= 24;

                _advice.SetText(
                    string.Format("sobra ~{0:0}% \u00B7 usa {1:0.#}{2}", _leftover.Value, _needed.Value * (_perDay ? 24 : 1), _perDay ? "%/d" : "%/h"),
                    ClaudeMetric.LEVELWARN
                    );
            }
            else
            {
                _advice.SetText("");
            }
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
            Status = usage.Status;

            if (_account.IsSwap && usage.SwapNumber > 0)
            {
                IsActive = usage.Active;
            }

            if (usage.Status != ClaudeUsageStatus.Ok)
            {
                // Se conservan los ultimos valores buenos de la semana; el aviso va en la primera
                // linea, resaltado como alerta.
                ClaudeMetric _target = _session ?? _week;

                if (_target != null)
                {
                    _target.SetText(StatusText(usage));
                    _target.SetAlert(true);
                }

                return;
            }

            LastUsage = usage;

            if (_session != null)
            {
                _session.SetAlert(false);
                _session.SetUsage(usage.SessionPercent, usage.SessionReset, usage.FetchedAt);
            }

            if (_week != null)
            {
                _week.SetAlert(false);
                _week.SetUsage(usage.WeekPercent, usage.WeekReset, usage.FetchedAt);
            }
        }

        private static string StatusText(ClaudeUsage usage)
        {
            switch (usage.Status)
            {
                case ClaudeUsageStatus.NoCredentials:
                    return "sin credenciales";

                case ClaudeUsageStatus.TokenExpired:
                    // Una cuenta inactiva de claude-swap se renueva con "claude-swap run N".
                    return usage.SwapNumber > 0 && !usage.Active
                        ? string.Format("token vencido: run {0}", usage.SwapNumber)
                        : "token vencido: abre claude";

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

        private ClaudeMetric _advice { get; set; }

        private bool _busy { get; set; } = false;

        private DateTime _nextFetch { get; set; } = DateTime.MinValue;

        private DateTime _fetchStarted { get; set; } = DateTime.MinValue;
    }

    /// <summary>
    /// Capacidad libre de las cuentas visibles, ponderada por plan (Pro = 1), y cual conviene usar.
    /// "Ahora" limita cada cuenta tambien por su ventana de 5 horas; "7d" solo por la semanal.
    /// </summary>
    public class ClaudeTotalMonitor : ClaudeBaseMonitor
    {
        private static readonly TimeSpan SWITCHCOOLDOWN = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DRAINCOOLDOWN = TimeSpan.FromMinutes(30);

        public ClaudeTotalMonitor(ClaudeMonitor[] accounts, MetricConfig[] metrics, ConfigParam[] parameters, bool showName) : base("claude-total", "Total", showName)
        {
            _accounts = accounts;

            _autoSwitch = parameters.GetValue<bool>(ParamKey.ClaudeAutoSwitch);
            _switchAt = parameters.GetValue<int>(ParamKey.ClaudeSwitchAt);
            _switchBelow = parameters.GetValue<int>(ParamKey.ClaudeSwitchBelow);
            _drainMode = parameters.GetValue<bool>(ParamKey.ClaudeDrainMode);
            _notifyHours = parameters.GetValue<int>(ParamKey.ClaudeNotifyHours);

            List<iMetric> _metrics = new List<iMetric>();

            _now = new ClaudeMetric(MetricKey.ClaudeSession, SESSION, "Ahora");
            _week = new ClaudeMetric(MetricKey.ClaudeWeek, WEEK, "7d");

            _metrics.Add(_now);
            _metrics.Add(_week);

            if (metrics.IsEnabled(MetricKey.ClaudeAdvice))
            {
                _end = new ClaudeMetric(MetricKey.ClaudeAdvice, WEEK);
                _metrics.Add(_end);
            }

            Metrics = _metrics.ToArray();
        }

        public override void Update()
        {
            ShowEnd();

            AutoSwitch();
            NotifyLeftovers();

            double _total = _accounts.Sum(a => a.Weight);

            string _name = string.Format("Total ({0:0.#} Pro)", _total);

            if (Name != _name)
            {
                Name = _name;
            }

            Show(_now, _accounts.Select(a => a.Remaining(true)).ToArray(), _total);
            Show(_week, _accounts.Select(a => a.Remaining(false)).ToArray(), _total);

            // Solo se atenua si ninguna cuenta tiene dato reciente; una cuenta vieja ya se ve atenuada en su fila.
            IsStale = _accounts.Where(a => a.LastUsage != null).All(a => a.IsStale) && _accounts.Any(a => a.LastUsage != null);

            // Recomendada: la que mas urge gastar. Solo se marca si supera claramente a la activa.
            ClaudeMonitor _best = MostUrgent(null);
            ClaudeMonitor _active = _accounts.FirstOrDefault(a => a.IsActive);

            bool _better = _best != null && _best != _active && (_active == null || ClearlyMoreUrgent(_best, _active));

            foreach (ClaudeMonitor _account in _accounts)
            {
                _account.IsRecommended = _better && _account == _best;
            }
        }

        // Margen para considerar que otra cuenta urge mas que la activa: evita cambiar por diferencias minimas.
        private const double URGENCYMARGIN = 1.25;

        /// <summary>
        /// La cuenta que mas capacidad necesita gastar por hora para no perder nada en su reinicio
        /// semanal, entre las que se pueden usar ahora (dato valido y margen en 5h y 7d).
        /// </summary>
        private ClaudeMonitor MostUrgent(ClaudeMonitor exclude)
        {
            return _accounts
                .Where(a => a != exclude && a.Status == ClaudeUsageStatus.Ok && a.LastUsage != null)
                .Where(a => (a.UsedPercent(false) ?? 0) < _switchAt && (a.UsedPercent(true) ?? 0) < _switchBelow)
                .Where(a => (a.NeededUnitsPerHour ?? 0) > 0)
                .OrderByDescending(a => a.NeededUnitsPerHour)
                .FirstOrDefault();
        }

        private static bool ClearlyMoreUrgent(ClaudeMonitor candidate, ClaudeMonitor current)
        {
            return (candidate.NeededUnitsPerHour ?? 0) > (current.NeededUnitsPerHour ?? 0) * URGENCYMARGIN;
        }

        // Aviso de Windows (globo del icono de bandeja) unas horas antes del reinicio semanal de una cuenta
        // a la que, al ritmo actual, le sobraria el 20 % o mas. Una sola vez por cuenta y reinicio.
        private void NotifyLeftovers()
        {
            if (_notifyHours <= 0 || DateTime.UtcNow < _nextNotifyCheck)
            {
                return;
            }

            _nextNotifyCheck = DateTime.UtcNow + TimeSpan.FromMinutes(1);

            foreach (ClaudeMonitor _account in _accounts)
            {
                double? _hours = _account.HoursToWeekReset;
                double? _percent = _account.LeftoverPercent;
                DateTime? _reset = _account.NextWeekReset;

                if (!_hours.HasValue || !_percent.HasValue || !_reset.HasValue || _account.Status != ClaudeUsageStatus.Ok)
                {
                    continue;
                }

                if (_hours.Value > _notifyHours || _percent.Value < ClaudeMonitor.LEFTOVERWARN)
                {
                    continue;
                }

                // El reinicio se redondea a la hora: claude-swap y la API lo dan con milisegundos distintos.
                string _key = string.Format("{0}|{1:yyyyMMddHH}", _account.ID, _reset.Value);

                if (!_notified.Add(_key))
                {
                    continue;
                }

                try
                {
                    App.TrayIcon?.ShowBalloonTip(
                        "Capacidad de Claude por perderse",
                        string.Format("{0} se reinicia en {1} y le sobraria ~{2:0}% al ritmo actual.", _account.BaseName, ClaudeMetric.FormatSpan(TimeSpan.FromHours(_hours.Value)), _percent.Value),
                        Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info
                        );
                }
                catch (Exception) { }
            }
        }

        // Las cuentas sin datos cuentan como agotadas: el total nunca promete capacidad que no se ve.
        private static void Show(ClaudeMetric metric, double?[] remaining, double total)
        {
            if (total <= 0 || remaining.All(r => !r.HasValue))
            {
                metric.SetText("...");
                return;
            }

            double _units = remaining.Sum(r => r ?? 0);
            double _percent = Math.Round(100 * _units / total);

            byte _level = _percent <= 10 ? ClaudeMetric.LEVELCRITICAL : (_percent <= 25 ? ClaudeMetric.LEVELWARN : ClaudeMetric.LEVELNORMAL);

            metric.SetText(string.Format("{0}% \u00B7 {1:0.#} Pro", _percent, _units), _level);
        }

        // Fila Fin del total: cuanto dura la capacidad semanal libre de todas las cuentas al ritmo de
        // consumo combinado y cuanta se perderia en los reinicios (en unidades Pro). Avisa si se acaba
        // antes del proximo reinicio semanal de cualquier cuenta, que es cuando vuelve a haber capacidad.
        private void ShowEnd()
        {
            if (_end == null)
            {
                return;
            }

            ClaudeMonitor[] _known = _accounts.Where(a => a.LastUsage != null).ToArray();

            if (_known.Length == 0)
            {
                _end.SetText("...");
                return;
            }

            double _rate = _known.Sum(a => a.WeekRate ?? 0);
            double _left = _known.Sum(a => a.Remaining(false) ?? 0);
            double _leftover = _known.Sum(a => a.LeftoverUnits ?? 0);
            double _total = _accounts.Sum(a => a.Weight);

            string _sobra = string.Format("sobra {0:0.#}", _leftover);

            if (_rate <= 0)
            {
                _end.SetText("ok \u00B7 " + _sobra);
                return;
            }

            TimeSpan _toEmpty = TimeSpan.FromHours(_left / _rate);

            DateTime? _nextReset = _known.Select(a => a.NextWeekReset).Where(r => r.HasValue).Min();

            bool _beforeReset = _nextReset.HasValue && DateTime.UtcNow + _toEmpty < _nextReset.Value;
            bool _wasting = _total > 0 && 100 * _leftover / _total >= ClaudeMonitor.LEFTOVERWARN;

            _end.SetText(
                string.Format("{0}{1} \u00B7 {2}", _beforeReset ? (char)0x26A0 + " " : "", ClaudeMetric.FormatSpan(_toEmpty), _sobra),
                _beforeReset || _wasting ? ClaudeMetric.LEVELWARN : ClaudeMetric.LEVELNORMAL
                );
        }

        // Cambio automatico, en dos casos:
        // 1. La cuenta activa llega al umbral (5h o 7d): pasa a la cuenta cuyo reinicio semanal esta mas
        //    cerca entre las que estan por debajo del maximo semanal.
        // 2. Con "agotar antes del reinicio": otra cuenta urge claramente mas que la activa (necesita
        //    gastar mas capacidad por hora para no perder nada). Espera mas entre cambios para no saltar
        //    de una cuenta a otra.
        private async void AutoSwitch()
        {
            if (!_autoSwitch || _switching || DateTime.UtcNow < _nextSwitch)
            {
                return;
            }

            ClaudeMonitor _active = _accounts.FirstOrDefault(a => a.IsActive);

            if (_active == null || _active.LastUsage == null || _active.IsStale || _active.Status != ClaudeUsageStatus.Ok)
            {
                return;
            }

            ClaudeMonitor _target = null;
            TimeSpan _cooldown = SWITCHCOOLDOWN;

            if ((_active.UsedPercent(false) ?? 0) >= _switchAt || (_active.UsedPercent(true) ?? 0) >= _switchAt)
            {
                _target = _accounts
                    .Where(a => a != _active && a.CanSwitch && a.Status == ClaudeUsageStatus.Ok && a.LastUsage != null)
                    .Where(a => (a.UsedPercent(true) ?? 0) < _switchBelow && (a.UsedPercent(false) ?? 0) < _switchAt)
                    .OrderBy(a => a.NextWeekReset ?? DateTime.MaxValue)
                    .FirstOrDefault();
            }
            else if (_drainMode)
            {
                ClaudeMonitor _urgent = MostUrgent(_active);

                if (_urgent != null && _urgent.CanSwitch && !_urgent.IsStale && ClearlyMoreUrgent(_urgent, _active))
                {
                    _target = _urgent;
                    _cooldown = DRAINCOOLDOWN;
                }
            }

            if (_target == null)
            {
                return;
            }

            _switching = true;

            string _error = await ClaudeMonitor.SwitchTo(_target.SwapEmail);

            _switching = false;
            _nextSwitch = DateTime.UtcNow + _cooldown;

            if (_error != null)
            {
                Debug.WriteLine("Cambio automatico de cuenta fallido: " + _error);
            }
        }

        private ClaudeMonitor[] _accounts { get; set; }

        private ClaudeMetric _now { get; set; }

        private ClaudeMetric _week { get; set; }

        private ClaudeMetric _end { get; set; }

        private bool _autoSwitch { get; set; }

        private int _switchAt { get; set; }

        private int _switchBelow { get; set; }

        private bool _drainMode { get; set; }

        private int _notifyHours { get; set; }

        private bool _switching { get; set; } = false;

        private DateTime _nextSwitch { get; set; } = DateTime.MinValue;

        private DateTime _nextNotifyCheck { get; set; } = DateTime.MinValue;

        // Avisos ya mostrados; estatico para no repetirlos cuando el sidebar se recarga.
        private static readonly HashSet<string> _notified = new HashSet<string>();
    }

    /// <summary>Confirmacion Si/No siempre al frente: el sidebar es una barra de aplicacion y un MessageBox puede quedar detras.</summary>
    public static class ClaudeConfirm
    {
        /// <summary>Con question = false es solo un aviso con un boton Aceptar.</summary>
        public static bool Show(Window owner, string title, string message, bool question = true)
        {
            Button _yes = new Button() { Content = question ? "S" + (char)0xED : "Aceptar", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
            Button _no = new Button() { Content = "No", IsCancel = true, MinWidth = 80, Padding = new Thickness(8, 3, 8, 3) };

            StackPanel _buttons = new StackPanel() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            _buttons.Children.Add(_yes);

            if (question)
            {
                _buttons.Children.Add(_no);
            }
            else
            {
                _yes.IsCancel = true;
            }

            StackPanel _panel = new StackPanel() { Margin = new Thickness(14), MaxWidth = 380 };
            _panel.Children.Add(new TextBlock() { Text = message, TextWrapping = TextWrapping.Wrap });
            _panel.Children.Add(_buttons);

            Window _window = new Window()
            {
                Title = title,
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

            _yes.Click += (s, e) => { _window.DialogResult = true; };

            return _window.ShowDialog() == true;
        }
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
