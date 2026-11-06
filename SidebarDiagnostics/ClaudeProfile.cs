using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        /// Dia del mes del cobro, puesto a mano. Manda sobre el calculado desde la creacion de la suscripcion,
        /// que el perfil no garantiza que sea la fecha de cobro.
        /// </summary>
        [JsonProperty]
        public int? ManualDay { get; set; }

        /// <summary>Fecha (aaaammdd) de la renovacion del ultimo aviso mostrado.</summary>
        [JsonProperty]
        public string AlertedFor { get; set; }

        /// <summary>Dias de antelacion (5 o 3) del ultimo aviso mostrado para esa renovacion.</summary>
        [JsonProperty]
        public int AlertedStage { get; set; }

        /// <summary>
        /// Proxima renovacion mensual estimada: el mismo dia y hora de la creacion de la suscripcion, cada mes.
        /// Falla si se cambio la fecha de facturacion; null si no hay fecha.
        /// </summary>
        public DateTime? NextRenewal(DateTime nowUtc)
        {
            if (ManualDay.HasValue)
            {
                // Inicio del dia de cobro, hora local; en meses cortos se usa el ultimo dia.
                DateTime _local = ClaudeSchedule.ToLocal(nowUtc);

                for (int i = 0; i < 3; i++)
                {
                    DateTime _month = new DateTime(_local.Year, _local.Month, 1).AddMonths(i);
                    DateTime _candidate = new DateTime(_month.Year, _month.Month, Math.Min(ManualDay.Value, DateTime.DaysInMonth(_month.Year, _month.Month)));

                    if (_candidate > _local)
                    {
                        return DateTime.SpecifyKind(_candidate, DateTimeKind.Local).ToUniversalTime();
                    }
                }
            }

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

        /// <summary>Fija a mano el dia del mes del cobro de una cuenta; null para volver al dato del perfil.</summary>
        public static void SetManualDay(string accountId, int? day)
        {
            lock (_lock)
            {
                Load();

                ClaudeProfile _profile;

                if (!_profiles.TryGetValue(accountId, out _profile))
                {
                    _profile = new ClaudeProfile() { FetchedAt = DateTime.MinValue };
                }

                _profile.ManualDay = day;
                _profile.AlertedFor = null;
                _profile.AlertedStage = 0;

                Save(accountId, _profile);
            }
        }

        /// <summary>Anota el aviso mostrado para que no se repita al reiniciar la aplicacion.</summary>
        public static void MarkAlerted(string accountId, string renewalDate, int stage)
        {
            lock (_lock)
            {
                Load();

                ClaudeProfile _profile;

                if (_profiles.TryGetValue(accountId, out _profile))
                {
                    _profile.AlertedFor = renewalDate;
                    _profile.AlertedStage = stage;

                    Save(accountId, _profile);
                }
            }
        }

        private static void Store(string accountId, ClaudeProfile profile)
        {
            lock (_lock)
            {
                Load();

                ClaudeProfile _old;

                // Lo puesto a mano y los avisos ya mostrados sobreviven a la lectura del perfil.
                if (_profiles.TryGetValue(accountId, out _old))
                {
                    profile.ManualDay = _old.ManualDay;
                    profile.AlertedFor = _old.AlertedFor;
                    profile.AlertedStage = _old.AlertedStage;
                }

                Save(accountId, profile);
            }
        }

        // Se llama con el candado tomado.
        private static void Save(string accountId, ClaudeProfile profile)
        {
            _profiles[accountId] = profile;

            try
            {
                Directory.CreateDirectory(ClaudeData.Dir);

                File.WriteAllText(FilePath, JsonConvert.SerializeObject(_profiles, Formatting.Indented));
            }
            catch (Exception) { }
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

    /// <summary>Ventana minima para escribir el dia de cobro de una cuenta.</summary>
    public static class ClaudeRenewalPrompt
    {
        /// <summary>
        /// Devuelve el dia del mes (1-31), 0 para quitar el dato puesto a mano, o null si se cancela. Acepta un
        /// numero o una fecha (dd/mm/aaaa o aaaa-mm-dd), de la que se toma el dia.
        /// </summary>
        public static int? Show(Window owner, string accountName, int? current)
        {
            TextBox _input = new TextBox() { Text = current.HasValue ? current.Value.ToString() : "", MinWidth = 280, Margin = new Thickness(0, 6, 0, 4), Padding = new Thickness(3) };
            TextBlock _error = new TextBlock() { Foreground = System.Windows.Media.Brushes.Firebrick, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed, Text = "Escribe un dia (1 a 31) o una fecha." };

            Button _ok = new Button() { Content = "Aceptar", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
            Button _cancel = new Button() { Content = "Cancelar", IsCancel = true, MinWidth = 80, Padding = new Thickness(8, 3, 8, 3) };

            StackPanel _buttons = new StackPanel() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _buttons.Children.Add(_ok);
            _buttons.Children.Add(_cancel);

            StackPanel _panel = new StackPanel() { Margin = new Thickness(14) };
            _panel.Children.Add(new TextBlock() { Text = string.Format("Dia del mes en que se renueva {0}\n(un numero o una fecha; vacio para usar el dato calculado)", accountName) });
            _panel.Children.Add(_input);
            _panel.Children.Add(_error);
            _panel.Children.Add(_buttons);

            Window _window = new Window()
            {
                Title = "Renovacion",
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

            int _day = 0;

            _ok.Click += (s, e) =>
            {
                if (TryParse(_input.Text, out _day))
                {
                    _window.DialogResult = true;
                }
                else
                {
                    _error.Visibility = Visibility.Visible;
                }
            };

            _window.Loaded += (s, e) =>
            {
                _input.Focus();
                _input.SelectAll();
            };

            return _window.ShowDialog() == true ? _day : (int?)null;
        }

        private static bool TryParse(string text, out int day)
        {
            day = 0;

            text = (text ?? "").Trim();

            if (text.Length == 0)
            {
                return true;
            }

            int _number;

            if (int.TryParse(text, out _number))
            {
                day = _number;

                return _number >= 1 && _number <= 31;
            }

            DateTime _date;

            if (DateTime.TryParse(text, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.None, out _date) || DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _date))
            {
                day = _date.Day;

                return true;
            }

            return false;
        }
    }
}
