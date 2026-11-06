using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SidebarDiagnostics.Monitoring
{
    /// <summary>
    /// Datos de suscripcion de una cuenta, del perfil de la cuenta. La fecha de cobro no viene en el perfil: la
    /// renovacion mensual se calcula desde el dia en que se creo la suscripcion.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class ClaudeProfile
    {
        [JsonProperty]
        public DateTime? SubscriptionCreatedAt { get; set; }

        /// <summary>Estado de la suscripcion (active, ...).</summary>
        [JsonProperty]
        public string Status { get; set; }

        /// <summary>Plan segun el limite de uso (p. ej. default_claude_max_5x).</summary>
        [JsonProperty]
        public string Tier { get; set; }

        [JsonProperty]
        public string BillingType { get; set; }

        /// <summary>Cuando se leyo el perfil.</summary>
        [JsonProperty]
        public DateTime FetchedAt { get; set; }

        /// <summary>
        /// Proxima renovacion mensual estimada: el mismo dia y hora de la creacion de la suscripcion, cada mes.
        /// Falla si se cambio la fecha de facturacion; null si no hay fecha.
        /// </summary>
        public DateTime? NextRenewal(DateTime nowUtc)
        {
            if (!SubscriptionCreatedAt.HasValue)
            {
                return null;
            }

            DateTime _start = new DateTime(SubscriptionCreatedAt.Value.Ticks, DateTimeKind.Utc);

            int _months = Math.Max(0, (nowUtc.Year - _start.Year) * 12 + nowUtc.Month - _start.Month);

            DateTime _next = _start.AddMonths(_months);

            // AddMonths parte siempre de la fecha original, asi un dia 31 no se corre a 28 de forma permanente.
            while (_next <= nowUtc)
            {
                _months++;

                _next = _start.AddMonths(_months);
            }

            return _next;
        }
    }

    /// <summary>
    /// Perfiles de las cuentas, por ID de cuenta, guardados en la carpeta interna de datos. El perfil casi no
    /// cambia: se consulta una vez al dia (30 minutos de espera si falla) y solo de las cuentas cuyo token
    /// se puede leer, es decir, la cuenta activa de claude-swap y las de su propia carpeta.
    /// </summary>
    public static class ClaudeProfileStore
    {
        private const string FILENAME = "claude-profiles.json";

        private static readonly TimeSpan REFRESH = TimeSpan.FromHours(24);
        private static readonly TimeSpan RETRY = TimeSpan.FromMinutes(30);

        private static readonly object _lock = new object();

        private static readonly Dictionary<string, DateTime> _attempts = new Dictionary<string, DateTime>();

        private static readonly HashSet<string> _running = new HashSet<string>();

        private static Dictionary<string, ClaudeProfile> _profiles;

        private static string FilePath
        {
            get { return Path.Combine(ClaudeData.Dir, FILENAME); }
        }

        public static ClaudeProfile Get(string accountId)
        {
            lock (_lock)
            {
                Load();

                ClaudeProfile _profile;

                return _profiles.TryGetValue(accountId, out _profile) ? _profile : null;
            }
        }

        /// <summary>
        /// Consulta el perfil si no se ha leido en el ultimo dia. No bloquea: la consulta corre aparte.
        /// expectedEmail, si se da, debe coincidir con el correo del perfil (la cuenta activa pudo cambiar).
        /// </summary>
        public static void RefreshIfNeeded(string accountId, string credentialsPath, string expectedEmail)
        {
            if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(credentialsPath))
            {
                return;
            }

            lock (_lock)
            {
                Load();

                ClaudeProfile _current;

                if (_profiles.TryGetValue(accountId, out _current) && DateTime.UtcNow - _current.FetchedAt < REFRESH)
                {
                    return;
                }

                DateTime _last;

                if ((_attempts.TryGetValue(accountId, out _last) && DateTime.UtcNow - _last < RETRY) || !_running.Add(accountId))
                {
                    return;
                }

                _attempts[accountId] = DateTime.UtcNow;
            }

            Task.Run(async () =>
            {
                try
                {
                    JObject _json = await ClaudeUsageClient.GetJsonAsync("/api/oauth/profile", credentialsPath).ConfigureAwait(false);

                    string _email = (string)_json?["account"]?["email"];

                    if (_json != null && (expectedEmail == null || string.Equals(_email, expectedEmail, StringComparison.OrdinalIgnoreCase)))
                    {
                        JToken _org = _json["organization"];

                        Store(accountId, new ClaudeProfile()
                        {
                            SubscriptionCreatedAt = ClaudeUsageClient.ParseDate(_org?["subscription_created_at"]),
                            Status = (string)_org?["subscription_status"],
                            Tier = (string)_org?["rate_limit_tier"],
                            BillingType = (string)_org?["billing_type"],
                            FetchedAt = DateTime.UtcNow
                        });
                    }
                }
                catch (Exception) { }
                finally
                {
                    lock (_lock)
                    {
                        _running.Remove(accountId);
                    }
                }
            });
        }

        private static void Store(string accountId, ClaudeProfile profile)
        {
            lock (_lock)
            {
                Load();

                _profiles[accountId] = profile;

                try
                {
                    Directory.CreateDirectory(ClaudeData.Dir);

                    File.WriteAllText(FilePath, JsonConvert.SerializeObject(_profiles, Formatting.Indented));
                }
                catch (Exception) { }
            }
        }

        // Se llama con el candado tomado.
        private static void Load()
        {
            if (_profiles != null)
            {
                return;
            }

            _profiles = new Dictionary<string, ClaudeProfile>();

            try
            {
                if (File.Exists(FilePath))
                {
                    Dictionary<string, ClaudeProfile> _saved = JsonConvert.DeserializeObject<Dictionary<string, ClaudeProfile>>(File.ReadAllText(FilePath));

                    if (_saved != null)
                    {
                        _profiles = _saved;
                    }
                }
            }
            catch (Exception) { }
        }
    }
}
