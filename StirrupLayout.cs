using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StripFootingRebar
{
    /// <summary>Un grupo de la distribucion de estribos: "8@100" (8 estribos cada 100 mm) o "R@200" (el resto cada 200 mm).</summary>
    public sealed class StirrupGroup
    {
        public int Count;
        public double SpacingMm;
        public bool Rest;
        public override string ToString() => (Rest ? "R" : Count.ToString()) + "@" + SpacingMm.ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Un tramo de estribos equiespaciados listo para Revit: cota del primero (desde la
    /// cara de inicio de la viga, pies), numero y separacion.
    /// </summary>
    public sealed class StirrupRun
    {
        public double W0;
        public int Count;
        public double Spacing;
        public string Label;
        public double Length => Count > 1 ? (Count - 1) * Spacing : 0;
        public IEnumerable<double> Stations()
        {
            for (int k = 0; k < Count; k++) yield return W0 + k * Spacing;
        }
    }

    /// <summary>
    /// Distribucion de los estribos a lo largo de la viga a partir de un texto como el de
    /// los planos: "1@50, 8@100, R@200" = el primero a 50 mm de la cara del apoyo, ocho mas
    /// cada 100 mm y el resto cada 200 mm (como maximo, repartidos por igual). Con
    /// "simetrico" los grupos se repiten desde el otro apoyo. Geometria pura, sin Revit.
    /// </summary>
    public static class StirrupLayout
    {
        private const double MmPerFt = 304.8;

        /// <summary>
        /// Lee la distribucion. Admite "1@50, 8@100, R@200", "1@.05+8@.10+R@.20" (metros si el
        /// valor es menor de 5), con "R", "r", "resto" o "rto" para el resto. Devuelve null y
        /// el motivo si no se entiende.
        /// </summary>
        public static List<StirrupGroup> Parse(string text, out string error)
        {
            error = null;
            var groups = new List<StirrupGroup>();
            if (string.IsNullOrWhiteSpace(text)) { error = "distribucion vacia"; return null; }
            string[] parts = text.Replace(";", ",").Replace("+", ",").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in parts)
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;
                int at = part.IndexOf('@');
                if (at < 0) { error = "\"" + part + "\" no tiene la forma N@separacion"; return null; }
                string left = part.Substring(0, at).Trim().ToLowerInvariant();
                string right = part.Substring(at + 1).Trim().ToLowerInvariant().Replace("mm", "").Replace("m", "").Trim();
                var g = new StirrupGroup();
                if (left == "r" || left == "resto" || left == "rto" || left == "rest") g.Rest = true;
                else if (!int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out g.Count) || g.Count < 1)
                { error = "\"" + part + "\": el numero de estribos tiene que ser un entero mayor que 0 (o R para el resto)"; return null; }
                if (!TryNumber(right, out double sp) || sp <= 0)
                { error = "\"" + part + "\": separacion no valida"; return null; }
                g.SpacingMm = sp < 5 ? sp * 1000 : sp;   // ".20" son metros
                groups.Add(g);
            }
            if (groups.Count == 0) { error = "distribucion vacia"; return null; }
            int rests = groups.Count(g => g.Rest);
            if (rests > 1) { error = "solo puede haber un grupo R (resto)"; return null; }
            if (rests == 1 && !groups[groups.Count - 1].Rest) { error = "el grupo R (resto) tiene que ser el ultimo"; return null; }
            return groups;
        }

        public static bool TryNumber(string s, out double v)
        {
            s = (s ?? "").Trim().Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        /// <summary>
        /// Longitud de un baston: "1500" (mm), "1.5" (metros si es menor de 5), "L/4" o
        /// "0.3L" (fraccion de la longitud de la viga). Devuelve mm.
        /// </summary>
        public static bool TryLength(string text, double beamLengthMm, out double mm, out string error)
        {
            mm = 0; error = null;
            string s = (text ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace(',', '.');
            if (s.Length == 0) { error = "longitud vacia"; return false; }
            if (s.Contains("l"))
            {
                double frac;
                if (s.StartsWith("l/"))
                {
                    if (!double.TryParse(s.Substring(2), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || n <= 0)
                    { error = "\"" + text + "\": se esperaba L/n"; return false; }
                    frac = 1 / n;
                }
                else if (s.EndsWith("l"))
                {
                    string f = s.Substring(0, s.Length - 1).TrimEnd('*', 'x');
                    if (f.Length == 0) frac = 1;
                    else if (!double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out frac) || frac <= 0)
                    { error = "\"" + text + "\": se esperaba una fraccion como 0.3L"; return false; }
                }
                else if (s.StartsWith("l*") || s.StartsWith("lx"))
                {
                    if (!double.TryParse(s.Substring(2), NumberStyles.Float, CultureInfo.InvariantCulture, out frac) || frac <= 0)
                    { error = "\"" + text + "\": se esperaba L*fraccion"; return false; }
                }
                else { error = "\"" + text + "\": longitud no valida (usa mm, metros, L/4 o 0.3L)"; return false; }
                if (frac > 1.5) { error = "\"" + text + "\": la fraccion de L es mayor que 1.5"; return false; }
                mm = frac * beamLengthMm;
                return true;
            }
            s = s.Replace("mm", "").Replace("m", "");
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0)
            { error = "\"" + text + "\": longitud no valida (usa mm, metros, L/4 o 0.3L)"; return false; }
            mm = v < 5 ? v * 1000 : v;
            return true;
        }

        public static string Format(IList<StirrupGroup> groups) => string.Join(", ", groups.Select(g => g.ToString()));

        /// <summary>
        /// Tramos de estribos de una viga de longitud "lengthFt" (pies). Los grupos se
        /// colocan desde la cara de inicio (mas "startOffset") y, si "symmetric", tambien
        /// desde la cara final (menos "endOffset"); el resto se reparte por igual en el hueco
        /// con una separacion no mayor que la del grupo R. Sin grupo R y sin simetria, el
        /// ultimo grupo se repite hasta el final. "warning" avisa si la viga es demasiado corta.
        /// </summary>
        public static List<StirrupRun> Runs(double lengthFt, double startOffsetFt, double endOffsetFt,
                                            IList<StirrupGroup> groups, bool symmetric, out string warning)
        {
            warning = null;
            var runs = new List<StirrupRun>();
            if (groups == null || groups.Count == 0) return runs;
            double wLow = startOffsetFt, wHigh = lengthFt - endOffsetFt;
            if (wHigh - wLow <= 0) { warning = "la viga no tiene longitud util para estribos"; return runs; }

            List<StirrupGroup> fixedGroups = groups.Where(g => !g.Rest).ToList();
            StirrupGroup rest = groups.FirstOrDefault(g => g.Rest);
            if (rest == null && !symmetric) rest = fixedGroups.Count > 0
                ? new StirrupGroup { Rest = true, SpacingMm = fixedGroups[fixedGroups.Count - 1].SpacingMm } : null;
            double mid = 0.5 * (wLow + wHigh);

            // --- desde el inicio ---
            double w = wLow;
            double wb = wLow;   // ultimo estribo colocado desde el inicio
            bool cut = false;
            foreach (StirrupGroup g in fixedGroups)
            {
                double sp = g.SpacingMm / MmPerFt;
                int n = 0;
                for (int k = 0; k < g.Count; k++)
                {
                    double wk = w + (k + 1) * sp;
                    if (wk > (symmetric ? mid : wHigh) + 1e-9) { cut = true; break; }
                    n++;
                }
                if (n > 0)
                {
                    runs.Add(new StirrupRun { W0 = w + sp, Count = n, Spacing = sp, Label = "inicio " + g });
                    w += n * sp;
                    wb = w;
                }
                if (cut) break;
            }

            // --- desde el final (mismos grupos en espejo) ---
            double wt = wHigh;   // primer estribo colocado desde el final
            var top = new List<StirrupRun>();
            bool cutTop = false;
            if (symmetric)
            {
                double ww = wHigh;
                foreach (StirrupGroup g in fixedGroups)
                {
                    double sp = g.SpacingMm / MmPerFt;
                    int n = 0;
                    for (int k = 0; k < g.Count; k++)
                    {
                        double wk = ww - (k + 1) * sp;
                        if (wk < Math.Max(mid, wb) - 1e-9) { cutTop = true; break; }
                        n++;
                    }
                    if (n > 0)
                    {
                        ww -= n * sp;
                        top.Add(new StirrupRun { W0 = ww, Count = n, Spacing = sp, Label = "fin " + g });
                        wt = ww;
                    }
                    if (cutTop) break;
                }
            }

            // --- resto, repartido por igual entre el ultimo del inicio y el primero del final ---
            if (rest != null)
            {
                double gap = wt - wb;
                double sp = rest.SpacingMm / MmPerFt;
                if (gap > 1e-9 && sp > 1e-9)
                {
                    int n = (int)Math.Ceiling(gap / sp - 1e-9);   // huecos
                    if (n >= 2)
                    {
                        double actual = gap / n;
                        runs.Add(new StirrupRun { W0 = wb + actual, Count = n - 1, Spacing = actual, Label = "resto " + rest + " (=" + Math.Round(actual * MmPerFt) + ")" });
                    }
                }
            }
            runs.AddRange(top.OrderBy(r => r.W0));
            if (cut || cutTop) warning = "la viga es demasiado corta para toda la distribucion: se han recortado grupos";
            return runs.OrderBy(r => r.W0).ToList();
        }

        /// <summary>Todas las cotas de estribos (desde la cara de inicio, pies), de principio a fin.</summary>
        public static List<double> Stations(IEnumerable<StirrupRun> runs) =>
            runs.SelectMany(r => r.Stations()).OrderBy(w => w).ToList();
    }
}
