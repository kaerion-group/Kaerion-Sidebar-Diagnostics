using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace SidebarDiagnostics.Monitoring
{
    /// <summary>
    /// Carpeta interna de los datos de Claude. Esta fuera de la carpeta de la aplicacion para que ni las
    /// actualizaciones ni una reinstalacion borren el historial.
    /// </summary>
    public static class ClaudeData
    {
        public static string Dir
        {
            get
            {
                if (_dir == null)
                {
                    _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KaerionSidebarDiagnostics");
                }

                return _dir;
            }
        }

        private static string _dir;
    }

    /// <summary>
    /// Parametros del modelo de estimacion, en claude-model.json (se crea con estos valores y se puede editar):
    /// horas muertas (sin uso intenso), porcentaje de la semana que cuesta vaciar una ventana de 5h por plan y
    /// horas que se tarda en vaciarla.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class ClaudeModelConfig
    {
        private const string FILENAME = "claude-model.json";

        [JsonProperty]
        public bool DeadEnabled { get; set; } = true;

        [JsonProperty]
        public int DeadFromHour { get; set; } = 2;

        [JsonProperty]
        public int DeadToHour { get; set; } = 6;

        /// <summary>Porcentaje de la semana que equivale a vaciar una ventana de 5h en Pro.</summary>
        [JsonProperty]
        public double CapPro { get; set; } = 17;

        /// <summary>Lo mismo en Max (5X y 20X).</summary>
        [JsonProperty]
        public double CapMax { get; set; } = 23;

        /// <summary>Horas que se tarda en vaciar una ventana de 5h con uso intenso.</summary>
        [JsonProperty]
        public double FullBurnHours { get; set; } = 3;

        public static ClaudeModelConfig Current
        {
            get
            {
                if (_current == null)
                {
                    _current = Load();
                }

                return _current;
            }
        }

        /// <summary>Valor inicial del tope de una ventana segun el peso del plan (Pro = 1).</summary>
        public double WindowCap(double weight)
        {
            return weight > 1 ? CapMax : CapPro;
        }

        private static ClaudeModelConfig Load()
        {
            string _path = Path.Combine(ClaudeData.Dir, FILENAME);

            ClaudeModelConfig _config = null;

            try
            {
                if (File.Exists(_path))
                {
                    _config = JsonConvert.DeserializeObject<ClaudeModelConfig>(File.ReadAllText(_path));
                }
            }
            catch (Exception) { }

            if (_config == null)
            {
                _config = new ClaudeModelConfig();

                try
                {
                    Directory.CreateDirectory(ClaudeData.Dir);
                    File.WriteAllText(_path, JsonConvert.SerializeObject(_config, Formatting.Indented));
                }
                catch (Exception) { }
            }

            _config.Validate();

            return _config;
        }

        private void Validate()
        {
            DeadFromHour = Math.Min(23, Math.Max(0, DeadFromHour));
            DeadToHour = Math.Min(23, Math.Max(0, DeadToHour));

            if (DeadFromHour == DeadToHour)
            {
                DeadEnabled = false;
            }

            CapPro = Math.Min(100, Math.Max(1, CapPro));
            CapMax = Math.Min(100, Math.Max(1, CapMax));
            FullBurnHours = Math.Min(5, Math.Max(0.5, FullBurnHours));
        }

        private static ClaudeModelConfig _current;
    }

    /// <summary>Calendario de horas activas: el reloj local sin la franja muerta (por defecto de 2 a 6 am).</summary>
    public static class ClaudeSchedule
    {
        public static bool IsDead(DateTime utc)
        {
            ClaudeModelConfig _config = ClaudeModelConfig.Current;

            if (!_config.DeadEnabled)
            {
                return false;
            }

            double _hour = ToLocal(utc).TimeOfDay.TotalHours;

            return _config.DeadFromHour < _config.DeadToHour
                ? _hour >= _config.DeadFromHour && _hour < _config.DeadToHour
                : _hour >= _config.DeadFromHour || _hour < _config.DeadToHour;
        }

        /// <summary>Horas entre dos instantes sin contar la franja muerta.</summary>
        public static double ActiveHours(DateTime fromUtc, DateTime toUtc)
        {
            if (toUtc <= fromUtc)
            {
                return 0;
            }

            double _total = (toUtc - fromUtc).TotalHours;

            ClaudeModelConfig _config = ClaudeModelConfig.Current;

            if (!_config.DeadEnabled || _total > 24 * 60)
            {
                return _total;
            }

            DateTime _from = ToLocal(fromUtc);
            DateTime _to = ToLocal(toUtc);

            double _dead = 0;

            // Se parte del dia anterior por si la franja cruza la medianoche.
            for (DateTime _day = _from.Date.AddDays(-1); _day <= _to.Date; _day = _day.AddDays(1))
            {
                DateTime _start = _day.AddHours(_config.DeadFromHour);
                DateTime _end = _day.AddHours(_config.DeadFromHour < _config.DeadToHour ? _config.DeadToHour : _config.DeadToHour + 24);

                DateTime _s = _start > _from ? _start : _from;
                DateTime _e = _end < _to ? _end : _to;

                if (_e > _s)
                {
                    _dead += (_e - _s).TotalHours;
                }
            }

            return Math.Max(0, _total - _dead);
        }

        /// <summary>El mismo instante si es hora activa; si no, cuando termina la franja muerta.</summary>
        public static DateTime NextActiveStart(DateTime utc)
        {
            if (!IsDead(utc))
            {
                return utc;
            }

            ClaudeModelConfig _config = ClaudeModelConfig.Current;

            DateTime _local = ToLocal(utc);
            DateTime _end = _local.Date.AddHours(_config.DeadToHour);

            if (_config.DeadFromHour > _config.DeadToHour && _local.TimeOfDay.TotalHours >= _config.DeadFromHour)
            {
                _end = _end.AddDays(1);
            }

            return utc + (_end - _local);
        }

        /// <summary>El instante en que se habran consumido las horas activas indicadas a partir de uno dado.</summary>
        public static DateTime AddActiveHours(DateTime startUtc, double hours)
        {
            DateTime _time = startUtc;
            double _left = hours;

            for (int i = 0; i < 4000 && _left > 0; i++)
            {
                _time = NextActiveStart(_time);

                DateTime _dead = NextDeadStart(_time);
                double _available = (_dead - _time).TotalHours;

                if (_left <= _available)
                {
                    return _time.AddHours(_left);
                }

                _left -= _available;
                _time = _dead;
            }

            return _time;
        }

        /// <summary>Horas hasta que empieza la proxima franja muerta; null si no hay franja o ya se esta en ella.</summary>
        public static double? HoursToDeadStart(DateTime utc)
        {
            if (!ClaudeModelConfig.Current.DeadEnabled || IsDead(utc))
            {
                return null;
            }

            return (NextDeadStart(utc) - utc).TotalHours;
        }

        /// <summary>Hora local de un instante UTC, para mostrarla.</summary>
        public static DateTime ToLocal(DateTime utc)
        {
            return new DateTime(utc.Ticks, DateTimeKind.Utc).ToLocalTime();
        }

        // Inicio de la siguiente franja muerta a partir de un instante activo.
        private static DateTime NextDeadStart(DateTime utc)
        {
            ClaudeModelConfig _config = ClaudeModelConfig.Current;

            if (!_config.DeadEnabled)
            {
                return utc.AddYears(10);
            }

            DateTime _local = ToLocal(utc);
            DateTime _start = _local.Date.AddHours(_config.DeadFromHour);

            if (_start <= _local)
            {
                _start = _start.AddDays(1);
            }

            return utc + (_start - _local);
        }
    }

    /// <summary>Una lectura de uso de una cuenta, tal como se guarda en el historial.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class ClaudeSample
    {
        [JsonProperty("t")]
        public DateTime Time { get; set; }

        /// <summary>Identificador de la cuenta; solo se rellena al escribir en el archivo.</summary>
        [JsonProperty("a")]
        public string Account { get; set; }

        [JsonProperty("sp")]
        public double? SessionPercent { get; set; }

        [JsonProperty("sr")]
        public DateTime? SessionReset { get; set; }

        [JsonProperty("wp")]
        public double? WeekPercent { get; set; }

        [JsonProperty("wr")]
        public DateTime? WeekReset { get; set; }
    }

    /// <summary>Ritmo de consumo semanal medido y cuantos datos lo respaldan.</summary>
    public sealed class ClaudeRate
    {
        /// <summary>Puntos de porcentaje semanal por hora activa.</summary>
        public double PerHour { get; set; }

        /// <summary>Horas activas de datos que respaldan el ritmo.</summary>
        public double CoverageHours { get; set; }

        public bool Confident
        {
            get { return CoverageHours >= ClaudeAccountHistory.MINCOVERAGE; }
        }
    }

    /// <summary>
    /// Historial de lecturas de todas las cuentas: se guarda completo en archivos mensuales (JSON por linea)
    /// en la carpeta interna y en memoria quedan los ultimos 35 dias.
    /// </summary>
    public static class ClaudeHistory
    {
        private static readonly object _lock = new object();

        private static readonly JsonSerializerSettings _json = new JsonSerializerSettings()
        {
            NullValueHandling = NullValueHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            DateParseHandling = DateParseHandling.DateTime
        };

        private static readonly Dictionary<string, ClaudeAccountHistory> _accounts = new Dictionary<string, ClaudeAccountHistory>();

        private static bool _loaded = false;

        public static ClaudeAccountHistory For(string id)
        {
            lock (_lock)
            {
                if (!_loaded)
                {
                    _loaded = true;

                    Load();
                }

                ClaudeAccountHistory _history;

                if (!_accounts.TryGetValue(id, out _history))
                {
                    _accounts[id] = _history = new ClaudeAccountHistory(id);
                }

                return _history;
            }
        }

        internal static void Append(string account, ClaudeSample sample)
        {
            try
            {
                ClaudeSample _line = new ClaudeSample()
                {
                    Time = sample.Time,
                    Account = account,
                    SessionPercent = sample.SessionPercent,
                    SessionReset = sample.SessionReset,
                    WeekPercent = sample.WeekPercent,
                    WeekReset = sample.WeekReset
                };

                string _dir = Path.Combine(ClaudeData.Dir, "history");

                Directory.CreateDirectory(_dir);

                string _path = Path.Combine(_dir, string.Format("claude-history-{0:yyyyMM}.jsonl", sample.Time));

                lock (_lock)
                {
                    File.AppendAllText(_path, JsonConvert.SerializeObject(_line, Formatting.None, _json) + "\n", new UTF8Encoding(false));
                }
            }
            catch (Exception) { }
        }

        // Los ultimos tres meses cubren siempre los 35 dias que se conservan en memoria.
        private static void Load()
        {
            try
            {
                string _dir = Path.Combine(ClaudeData.Dir, "history");

                if (!Directory.Exists(_dir))
                {
                    return;
                }

                DateTime _now = DateTime.UtcNow;
                DateTime _since = _now - ClaudeAccountHistory.KEEP;

                for (int i = 2; i >= 0; i--)
                {
                    string _path = Path.Combine(_dir, string.Format("claude-history-{0:yyyyMM}.jsonl", _now.AddMonths(-i)));

                    if (!File.Exists(_path))
                    {
                        continue;
                    }

                    foreach (string _line in File.ReadLines(_path))
                    {
                        try
                        {
                            ClaudeSample _sample = JsonConvert.DeserializeObject<ClaudeSample>(_line, _json);

                            if (_sample == null || string.IsNullOrEmpty(_sample.Account) || _sample.Time < _since)
                            {
                                continue;
                            }

                            ClaudeAccountHistory _history;

                            if (!_accounts.TryGetValue(_sample.Account, out _history))
                            {
                                _accounts[_sample.Account] = _history = new ClaudeAccountHistory(_sample.Account);
                            }

                            _history.Restore(_sample);
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
        }
    }

    /// <summary>Historial de una cuenta y las estimaciones que salen de el.</summary>
    public class ClaudeAccountHistory
    {
        /// <summary>Horas activas de datos a partir de las cuales el ritmo se considera fiable.</summary>
        public const double MINCOVERAGE = 3;

        public static readonly TimeSpan KEEP = TimeSpan.FromDays(35);

        // Las estimaciones usan como maximo los ultimos 7 dias; lo mas reciente pesa mas.
        private static readonly TimeSpan LOOKBACK = TimeSpan.FromDays(7);
        private const double HALFLIFE = 36;

        // Una lectura sin cambios se vuelve a guardar como mucho cada 10 minutos.
        private static readonly TimeSpan IDLESAVE = TimeSpan.FromMinutes(10);

        public ClaudeAccountHistory(string id)
        {
            _id = id;
        }

        /// <summary>Carga una lectura leida del archivo; no la vuelve a escribir.</summary>
        internal void Restore(ClaudeSample sample)
        {
            if (_samples.Count == 0 || _samples[_samples.Count - 1].Time < sample.Time)
            {
                _samples.Add(sample);
            }
        }

        /// <summary>Guarda la lectura en memoria y en el archivo si trae algo nuevo.</summary>
        public void Add(ClaudeUsage usage)
        {
            if (usage == null || (!usage.WeekPercent.HasValue && !usage.SessionPercent.HasValue))
            {
                return;
            }

            DateTime _time = usage.FetchedAt ?? DateTime.UtcNow;

            ClaudeSample _last = _samples.Count > 0 ? _samples[_samples.Count - 1] : null;

            if (_last != null && _time <= _last.Time)
            {
                return;
            }

            ClaudeSample _sample = new ClaudeSample()
            {
                Time = new DateTime(_time.Ticks, DateTimeKind.Utc),
                SessionPercent = usage.SessionPercent,
                SessionReset = usage.SessionReset,
                WeekPercent = usage.WeekPercent,
                WeekReset = usage.WeekReset
            };

            bool _same = _last != null
                && _last.SessionPercent == _sample.SessionPercent
                && _last.WeekPercent == _sample.WeekPercent
                && SameReset(_last.SessionReset, _sample.SessionReset)
                && SameReset(_last.WeekReset, _sample.WeekReset);

            if (_same && _sample.Time - _last.Time < IDLESAVE)
            {
                return;
            }

            _samples.Add(_sample);
            _samples.RemoveAll(s => s.Time < _sample.Time - KEEP);

            ClaudeHistory.Append(_id, _sample);
        }

        /// <summary>
        /// Ritmo semanal en puntos por hora activa. Usa lo que haya: con pocas horas de datos, esas; con siete
        /// dias, los siete, dando mas peso a lo reciente. Sin datos suficientes recurre al promedio de la semana
        /// en curso; null si no hay nada que medir.
        /// </summary>
        public ClaudeRate WeekRate(DateTime now, double? weekUsed, DateTime? weekReset)
        {
            DateTime _since = now - LOOKBACK;

            double _num = 0;
            double _den = 0;
            double _coverage = 0;

            for (int i = 1; i < _samples.Count; i++)
            {
                ClaudeSample _a = _samples[i - 1];
                ClaudeSample _b = _samples[i];

                if (_b.Time <= _since || !_a.WeekPercent.HasValue || !_b.WeekPercent.HasValue || !_a.WeekReset.HasValue || !_b.WeekReset.HasValue)
                {
                    continue;
                }

                // Solo pares de la misma semana; el salto de un reinicio no es consumo.
                if (!SameReset(_a.WeekReset, _b.WeekReset))
                {
                    continue;
                }

                double _delta = _b.WeekPercent.Value - _a.WeekPercent.Value;

                double _hours = ClaudeSchedule.ActiveHours(_a.Time > _since ? _a.Time : _since, _b.Time);

                // Un hueco de mas de 24 horas activas (la app cerrada) no dice nada del ritmo.
                if (_delta < 0 || _hours <= 0 || _hours > 24)
                {
                    continue;
                }

                double _weight = Math.Pow(0.5, ClaudeSchedule.ActiveHours(_b.Time, now) / HALFLIFE);

                _num += _weight * _delta;
                _den += _weight * _hours;
                _coverage += _hours;
            }

            if (_coverage >= MINCOVERAGE && _den > 0)
            {
                return new ClaudeRate() { PerHour = _num / _den, CoverageHours = _coverage };
            }

            if (weekUsed.HasValue && weekReset.HasValue)
            {
                double _elapsed = ClaudeSchedule.ActiveHours(weekReset.Value - TimeSpan.FromDays(7), now);

                if (_elapsed >= MINCOVERAGE)
                {
                    return new ClaudeRate() { PerHour = weekUsed.Value / _elapsed, CoverageHours = _elapsed };
                }
            }

            if (_coverage >= 1 && _den > 0)
            {
                return new ClaudeRate() { PerHour = _num / _den, CoverageHours = _coverage };
            }

            return null;
        }

        /// <summary>
        /// Porcentaje de la semana que costo en realidad vaciar una ventana de 5h, medido en las ventanas que
        /// se vieron casi completas (mediana de las ultimas 12); null si aun no hay suficientes.
        /// </summary>
        public double? CalibratedCap()
        {
            List<double> _caps = new List<double>();

            int _start = 0;

            for (int i = 1; i <= _samples.Count; i++)
            {
                bool _end = i == _samples.Count || !SameReset(_samples[i].SessionReset, _samples[_start].SessionReset) || !SameReset(_samples[i].WeekReset, _samples[_start].WeekReset);

                if (!_end)
                {
                    continue;
                }

                ClaudeSample _first = _samples[_start];
                ClaudeSample _last = _samples[i - 1];

                if (i - _start >= 3 && _first.SessionPercent.HasValue && _last.SessionPercent.HasValue && _first.WeekPercent.HasValue && _last.WeekPercent.HasValue)
                {
                    double _ds = _last.SessionPercent.Value - _first.SessionPercent.Value;
                    double _dw = _last.WeekPercent.Value - _first.WeekPercent.Value;

                    // Con poco recorrido el redondeo a enteros del porcentaje domina la medida.
                    if (_ds >= 40 && _dw >= 6)
                    {
                        _caps.Add(_dw * 100 / _ds);
                    }
                }

                _start = i;
            }

            if (_caps.Count < 3)
            {
                return null;
            }

            double[] _recent = _caps.Skip(Math.Max(0, _caps.Count - 12)).OrderBy(c => c).ToArray();

            double _median = _recent[_recent.Length / 2];

            return Math.Min(45, Math.Max(8, _median));
        }

        private static bool SameReset(DateTime? a, DateTime? b)
        {
            if (!a.HasValue || !b.HasValue)
            {
                return a.HasValue == b.HasValue;
            }

            return Math.Abs((a.Value - b.Value).TotalHours) < 2;
        }

        private string _id { get; set; }

        private List<ClaudeSample> _samples { get; set; } = new List<ClaudeSample>();
    }

    /// <summary>
    /// Cuanto se puede y cuanto hace falta gastar de la semana de una cuenta antes de su reinicio, contando
    /// las ventanas de 5h que caben y las horas muertas. Todo en porcentaje de la semana.
    /// </summary>
    public sealed class ClaudeEstimate
    {
        private static readonly TimeSpan SESSION = TimeSpan.FromHours(5);

        /// <summary>Lo que queda sin usar de la semana.</summary>
        public double Needed { get; private set; }

        /// <summary>Porcentaje de la semana que cuesta vaciar una ventana de 5h.</summary>
        public double Cap { get; private set; }

        /// <summary>Lo maximo que se puede gastar hasta el reinicio con las ventanas que quedan.</summary>
        public double MaxCapacity { get; private set; }

        /// <summary>Lo que se perdera aunque se use todo lo posible.</summary>
        public double Inevitable { get; private set; }

        /// <summary>Lo que se perdera al ritmo actual pero todavia se puede gastar.</summary>
        public double Avoidable { get; private set; }

        /// <summary>Lo que se perdera al ritmo actual (evitable mas inevitable).</summary>
        public double Leftover
        {
            get { return Inevitable + Avoidable; }
        }

        /// <summary>Fraccion de la capacidad maxima que hay que gastar; mas de 1 es perdida segura.</summary>
        public double Load { get; private set; }

        /// <summary>Ventanas de holgura: capacidad maxima menos lo necesario, entre el tope de una ventana.</summary>
        public double SlackWindows { get; private set; }

        /// <summary>Ritmo medido, en puntos por hora activa; null sin datos.</summary>
        public double? CurrentRate { get; private set; }

        /// <summary>Ritmo necesario para llegar justo al 100 % al reinicio, en puntos por hora activa.</summary>
        public double RequiredRate { get; private set; }

        /// <summary>Ritmo al vaciar una ventana a tope, en puntos por hora.</summary>
        public double MaxRate { get; private set; }

        /// <summary>Ultimo momento para empezar a gastar sin perder capacidad; null si ya no hay margen.</summary>
        public DateTime? Deadline { get; private set; }

        /// <summary>Horas activas hasta el reinicio semanal.</summary>
        public double ActiveHoursLeft { get; private set; }

        /// <summary>El ritmo medido tiene datos suficientes para fiarse de las alertas de sobrante.</summary>
        public bool Confident { get; private set; }

        public static ClaudeEstimate Compute(DateTime now, double weekUsed, DateTime weekReset, double sessionUsed, DateTime? sessionReset, double cap, ClaudeRate rate)
        {
            ClaudeEstimate _e = new ClaudeEstimate();

            double _fullBurn = ClaudeModelConfig.Current.FullBurnHours;

            double _needed = Math.Max(0, 100 - weekUsed);

            // Capacidad que se puede gastar si se empieza a gastar en el instante T (>= ahora).
            Func<DateTime, double> _capacity = (t) =>
            {
                double _total = 0;
                DateTime _prevEnd = t;

                if (sessionReset.HasValue && sessionReset.Value > t)
                {
                    double _activeH = ClaudeSchedule.ActiveHours(t, sessionReset.Value < weekReset ? sessionReset.Value : weekReset);
                    double _leftFraction = Math.Max(0, 1 - sessionUsed / 100);

                    _total += cap * Math.Min(_leftFraction, _activeH / _fullBurn);
                    _prevEnd = sessionReset.Value;
                }

                // Cada ventana nueva empieza en cuanto termina la anterior, salvo que caiga en la franja muerta.
                DateTime _start = ClaudeSchedule.NextActiveStart(_prevEnd);

                for (int i = 0; i < 64 && _start < weekReset; i++)
                {
                    DateTime _end = _start + SESSION;
                    double _activeH = ClaudeSchedule.ActiveHours(_start, _end < weekReset ? _end : weekReset);

                    _total += cap * Math.Min(1, _activeH / _fullBurn);
                    _start = ClaudeSchedule.NextActiveStart(_end);
                }

                return _total;
            };

            double _max = _capacity(now);

            double _activeLeft = ClaudeSchedule.ActiveHours(now, weekReset);

            double _projected = rate != null ? rate.PerHour * _activeLeft : 0;
            double _reachable = Math.Min(_needed, _max);

            _projected = Math.Min(_projected, _reachable);

            _e.Needed = _needed;
            _e.Cap = cap;
            _e.MaxCapacity = _max;
            _e.Inevitable = Math.Max(0, _needed - _max);
            _e.Avoidable = _reachable - _projected;
            _e.Load = _max > 0 ? Math.Min(99, _needed / _max) : (_needed > 0 ? 99 : 0);
            _e.SlackWindows = (_max - _needed) / cap;
            _e.CurrentRate = rate != null ? rate.PerHour : (double?)null;
            _e.RequiredRate = _needed / Math.Max(0.25, _activeLeft);
            _e.MaxRate = cap / _fullBurn;
            _e.ActiveHoursLeft = _activeLeft;
            _e.Confident = rate != null && rate.Confident;

            if (_needed > 0 && _max >= _needed)
            {
                // Busqueda binaria del ultimo instante desde el que todavia alcanza la capacidad.
                long _lo = now.Ticks;
                long _hi = weekReset.Ticks;

                for (int i = 0; i < 30 && _hi - _lo > TimeSpan.TicksPerSecond; i++)
                {
                    long _mid = _lo + (_hi - _lo) / 2;

                    if (_capacity(new DateTime(_mid, DateTimeKind.Utc)) >= _needed)
                    {
                        _lo = _mid;
                    }
                    else
                    {
                        _hi = _mid;
                    }
                }

                _e.Deadline = new DateTime(_lo, DateTimeKind.Utc);
            }

            return _e;
        }
    }
}
