using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FootingRebar
{
    /// <summary>
    /// Una barra corrida partida en trozos empalmados por traslape porque es mas larga que
    /// la barra comercial. Todo en pies; w a lo largo del recorrido desde el inicio del
    /// primer tramo.
    /// </summary>
    public sealed class SplicedBar
    {
        /// <summary>Trozos (w0, w1) de inicio a fin; dos consecutivos se solapan la longitud de empalme.</summary>
        public List<(double w0, double w1)> Pieces = new List<(double w0, double w1)>();
        /// <summary>Centro (w) de cada empalme, en el mismo orden que los trozos.</summary>
        public List<double> Centers = new List<double>();
        /// <summary>Longitud de empalme (pies).</summary>
        public double Lap;
        /// <summary>Longitud en w de la bayoneta con la que el segundo trozo vuelve a la linea del primero (pies).</summary>
        public double Jog;
        public List<string> Warnings = new List<string>();
        public bool Spliced => Centers.Count > 0;
    }

    /// <summary>
    /// Empalmes por traslape de las barras corridas (ACI 318-19). Geometria pura, sin Revit.
    ///
    ///  - Longitud de desarrollo a traccion ld segun 25.4.2.3 (expresiones simplificadas):
    ///    ld = fy·ψt·ψg / (2.1·λ·√f'c) · db hasta 3/4" y / (1.7·λ·√f'c) para mayores (MPa),
    ///    con ψt = 1.3 si hay mas de 300 mm de hormigon fresco bajo la barra, ψe = λ = 1,
    ///    ψg segun el grado del acero y √f'c ≤ 8.3 MPa; ld ≥ 300 mm.
    ///  - Longitud de empalme lst (25.5.2.1): clase B = 1.3 ld (todas las barras empalmadas en
    ///    la misma seccion, lo habitual), clase A = 1.0 ld; nunca menor de 300 mm.
    ///  - Zona de empalme en cada tramo del recorrido (entre columnas o esquinas): tercio
    ///    central del tramo, o cuartos extremos fuera de 2h desde la cara del apoyo.
    ///  - Cada trozo mide como mucho la longitud comercial; los empalmes se reparten lo mas
    ///    uniformemente posible y cada uno se lleva a la zona permitida mas cercana.
    /// </summary>
    public static class SpliceLayout
    {
        public const double MinLapMm = 300;
        /// <summary>Pendiente de la bayoneta del empalme: un diametro en vertical por JogSlope diametros en horizontal.</summary>
        public const double JogSlope = 6;
        private const double FtToMm = 304.8;
        private const double KgCm2ToMPa = 0.0980665;

        private static string Mm(double ft) => Math.Round(ft * FtToMm).ToString(CultureInfo.InvariantCulture);

        /// <summary>Longitud de desarrollo a traccion ld (mm) segun ACI 318-19 25.4.2.3 para una barra de diametro dbMm.</summary>
        public static double DevelopmentLengthMm(double dbMm, double fcKgCm2, double fyKgCm2, bool topBar)
        {
            double fc = Math.Max(1e-6, fcKgCm2) * KgCm2ToMPa, fy = Math.Max(0, fyKgCm2) * KgCm2ToMPa;
            double sqrt = Math.Min(Math.Sqrt(fc), 8.3);                                  // 25.4.1.4
            double psiT = topBar ? 1.3 : 1.0;                                           // 25.4.2.5: barra alta
            double psiG = fy <= 420 + 1e-6 ? 1.0 : fy <= 550 + 1e-6 ? 1.15 : 1.3;      // grado 60 / 80 / 100
            double k = dbMm <= 19.05 + 1e-6 ? 2.1 : 1.7;                               // hasta No. 6 (3/4") / No. 7 y mayores
            double ld = fy * psiT * psiG / (k * sqrt) * dbMm;
            return Math.Max(ld, MinLapMm);
        }

        /// <summary>
        /// Longitud de empalme a traccion (mm) de una barra de diametro dbMm: la fija de la
        /// configuracion si la hay, o 1.3 ld (clase B) / 1.0 ld (clase A), minimo 300 mm,
        /// redondeada hacia arriba a 50 mm. "topBar" = mas de 300 mm de hormigon fresco bajo la barra.
        /// </summary>
        public static double LapLengthMm(SpliceCfg cfg, double dbMm, bool topBar)
        {
            if (cfg == null || dbMm <= 0) return 0;
            if (cfg.FixedLengthMm > 0) return cfg.FixedLengthMm;
            double ld = DevelopmentLengthMm(dbMm, cfg.FcKgCm2, cfg.FyKgCm2, topBar);
            double lst = Math.Max((cfg.ClassB ? 1.3 : 1.0) * ld, MinLapMm);
            return Math.Ceiling(lst / 50 - 1e-9) * 50;
        }

        /// <summary>
        /// Parte la barra que va de w0 a w1 en trozos de como mucho la longitud comercial,
        /// solapados "lap", con cada empalme en la zona permitida de alguno de los "spans"
        /// (tramos del recorrido entre columnas o esquinas, en cotas w): el tercio central
        /// del tramo (centerZone) o sus cuartos extremos fuera de 2h del apoyo ("h" = canto
        /// maximo). Se usan los empalmes justos, repartidos lo mas uniformemente posible y
        /// llevados cada uno a la zona mas cercana; si asi no caben se reparten por igual a
        /// lo largo de la barra y se avisa. Sin empalmes devuelve un unico trozo.
        /// </summary>
        public static SplicedBar Plan(SpliceCfg cfg, double lap, double db, IList<(double w0, double w1)> spans, double h,
                                      double w0, double w1, bool centerZone, string name, double tol)
        {
            var sb = new SplicedBar { Lap = lap, Jog = JogSlope * db };
            sb.Pieces.Add((w0, w1));
            double Lc = (cfg?.CommercialLengthMm ?? 0) / FtToMm;
            double Lb = w1 - w0;
            if (Lc <= 0 || Lb <= Lc + tol) return sb;
            if (lap <= 0 || Lc - lap <= tol)
            {
                sb.Warnings.Add(name + ": la longitud comercial (" + Mm(Lc) + " mm) no da para empalmar con " + Mm(lap) +
                                " mm de empalme; la barra de " + Mm(Lb) + " mm se crea de una pieza");
                return sb;
            }
            int pMin = Math.Max(2, (int)Math.Ceiling((Lb - lap) / (Lc - lap) - 1e-9));   // trozos necesarios
            double half = 0.5 * lap;

            // zonas permitidas para el CENTRO del empalme (ya descontado medio solape), tramo a tramo
            var zones = new List<(double a, double b, bool forced)>();
            void AddZone(double a, double b, bool forced)
            {
                a = Math.Max(a, w0 + half);
                b = Math.Min(b, w1 - half);
                if (b >= a - tol) zones.Add((Math.Min(a, b), Math.Max(a, b), forced));
            }
            if (spans == null || spans.Count == 0) spans = new List<(double w0, double w1)> { (Math.Min(0, w0), Math.Max(w1, 0)) };
            foreach ((double s0, double s1) in spans)
            {
                double Ls = s1 - s0;
                if (Ls <= tol) continue;
                if (centerZone)
                {
                    AddZone(s0 + Ls / 3 + half, s0 + 2 * Ls / 3 - half, false);
                }
                else
                {
                    // lo mas lejos del apoyo que deja el cuarto extremo (el empalme acaba en L/4), pero fuera de 2h desde la cara
                    double a1 = s0 + 2 * h + half, b1 = s0 + 0.25 * Ls - half;
                    double a2 = s0 + 0.75 * Ls + half, b2 = s1 - 2 * h - half;
                    if (b1 >= a1 - tol) AddZone(a1, b1, false); else AddZone(a1, a1, true);
                    if (b2 >= a2 - tol) AddZone(a2, b2, false); else AddZone(b2, b2, true);
                }
            }
            zones = zones.OrderBy(z => z.a).ToList();

            // reparto uniforme de los empalmes y cada uno a la zona mas cercana; mas trozos si asi no caben
            if (zones.Count > 0)
                for (int p = pMin; p <= pMin + 2; p++)
                {
                    double Lp = (Lb + (p - 1) * lap) / p;
                    var centers = new List<double>();
                    bool forced = false, ok = true;
                    for (int k = 1; k < p; k++)
                    {
                        double target = w0 + k * (Lp - lap) + half;
                        (double a, double b, bool f) best = zones[0];
                        double bestD = double.MaxValue;
                        foreach (var z in zones)
                        {
                            double d = target < z.a ? z.a - target : target > z.b ? target - z.b : 0;
                            if (d < bestD - 1e-9) { bestD = d; best = z; }
                        }
                        double c = Math.Max(best.a, Math.Min(best.b, target));
                        if (centers.Count > 0 && c - half < centers[centers.Count - 1] + half - tol) { ok = false; break; }
                        centers.Add(c);
                        forced |= best.f;
                    }
                    if (!ok) continue;
                    List<(double w0, double w1)> pieces = Pieces(w0, w1, centers, half);
                    if (pieces.Any(q => q.w1 - q.w0 > Lc + tol)) continue;
                    sb.Pieces = pieces;
                    sb.Centers = centers;
                    if (forced) sb.Warnings.Add(name + ": algun empalme se sale del cuarto extremo (L/4) de su tramo para quedar fuera de 2h desde la cara del apoyo");
                    return sb;
                }

            // no caben en las zonas: reparto uniforme con los empalmes justos, y aviso
            {
                int p = pMin;
                double Lp = (Lb + (p - 1) * lap) / p;
                var centers = new List<double>();
                for (int k = 1; k < p; k++) centers.Add(w0 + k * (Lp - lap) + half);
                sb.Pieces = Pieces(w0, w1, centers, half);
                sb.Centers = centers;
                sb.Warnings.Add(name + ": la barra de " + Mm(Lb) + " mm necesita " + (p - 1) + " empalme(s) de " + Mm(lap) +
                                " mm que no caben en " + (centerZone ? "el tercio central" : "los cuartos extremos") + " de los tramos con barras de " +
                                Mm(Lc) + " mm: se reparten por igual a lo largo de la barra (w=" +
                                string.Join(", ", centers.Select(Mm)) + " mm), revisa la zona de empalme");
            }
            return sb;
        }

        private static List<(double w0, double w1)> Pieces(double w0, double w1, List<double> centers, double half)
        {
            var list = new List<(double w0, double w1)>();
            double a = w0;
            foreach (double c in centers)
            {
                list.Add((a, c + half));
                a = c - half;
            }
            list.Add((a, w1));
            return list;
        }
    }
}
