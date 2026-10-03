using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StripFootingRebar
{
    public enum BarKind { Corner, Intermediate, Baston, Side }

    /// <summary>
    /// Una barra longitudinal en la seccion: posicion u (eje) y distancia del eje a la cara
    /// del alma de su lado (asi la barra sigue la cara en las vigas de canto variable).
    /// </summary>
    public sealed class PlanBar
    {
        public double U;
        /// <summary>Distancia del eje de la barra a la cara superior (Top) o inferior del alma, siempre positiva.</summary>
        public double FaceOffset;
        public double Db;
        public string TypeName = "";
        public bool Top;
        /// <summary>Capa (1 = pegada al estribo).</summary>
        public int Layer;
        public BarKind Kind;
        /// <summary>Indice del baston en la lista de la configuracion; -1 en las barras corridas.</summary>
        public int Baston = -1;
        /// <summary>Identidad estable de la barra en la seccion (cara, capa, clase, baston, orden en u): sirve para asignarle otro tipo viga a viga.</summary>
        public string Key = "";
        /// <summary>True si esta barra lleva un tipo asignado a mano en esta viga.</summary>
        public bool Assigned;

        public double V(Rect web) => Top ? web.V2 - FaceOffset : web.V1 + FaceOffset;
        public Pt P(Rect web) => new Pt(U, V(web));
        public bool IsBaston => Kind == BarKind.Baston;

        public bool IsSide => Kind == BarKind.Side;
        public string Label => IsSide ? "barra lateral par " + Layer
            : (IsBaston ? "baston " + (Baston + 1) : Kind == BarKind.Corner ? "barra extrema" : "barra intermedia") +
              " " + (Top ? "superior" : "inferior") + " capa " + Layer;
    }

    /// <summary>Una capa de barras de una cara, ya apilada.</summary>
    public sealed class PlanLayer
    {
        public bool Top;
        public int Index;
        /// <summary>Distancia de la cara del alma a la superficie exterior de la capa (recubrimiento + estribo en la capa 1).</summary>
        public double Outer;
        /// <summary>Distancia de la cara del alma a la superficie interior de la capa (la siguiente se apila a partir de aqui).</summary>
        public double Inner;
        public List<PlanBar> Bars = new List<PlanBar>();
        /// <summary>Barras corridas de la capa.</summary>
        public int Main;
        /// <summary>Barras de baston que no cupieron.</summary>
        public int Missing;
        public string Name => (Top ? "S" : "I") + Index;
    }

    /// <summary>Todo lo que necesita BeamPlan.Build (en pies).</summary>
    public sealed class PlanOptions
    {
        /// <summary>Alma de referencia: la de menor canto, contra la que se comprueba que caben las capas.</summary>
        public Rect Web;
        public double Cover, Ds;
        /// <summary>Separacion libre entre capas y minima entre barras de una capa (ademas nunca menor que un diametro).</summary>
        public double LayerClear, MinClear;
        public FaceCfg Top, Bottom;
        public SideBarsCfg Sides;
        /// <summary>Pares de laterales propios de esta viga; -1 = el general.</summary>
        public int SideOverride = -1;
        public IList<BastonCfg> Bastones;
        /// <summary>Diametro (pies) de un tipo de barra por su nombre; 0 si no existe o esta vacio.</summary>
        public Func<string, double> Diameter;
        /// <summary>Barras propias de esta viga para (cara superior, capa 1..); -1 = el general.</summary>
        public Func<bool, int, int> CountOverride;
        /// <summary>Tipo de barra asignado a mano a una barra concreta de esta viga (por su Key); null = el general.</summary>
        public Func<string, string> TypeOverride;
        /// <summary>True si los bastones i y j coinciden en algun tramo de la viga (solo entonces se estorban en la seccion); null = todos coinciden.</summary>
        public Func<int, int, bool> BastonsOverlap;

        public bool Overlap(int i, int j) => i == j || BastonsOverlap == null || BastonsOverlap(i, j);
        /// <summary>Diametro orientativo para dibujar las barras sin tipo elegido (0 = no se colocan).</summary>
        public double FallbackDb;
        public double Tol;

        public double Dia(string name)
        {
            double d = Diameter != null && !string.IsNullOrWhiteSpace(name) ? Diameter(name) : 0;
            return d > 0 ? d : FallbackDb;
        }

        public int CountFor(bool top, int layer)
        {
            int c = CountOverride != null ? CountOverride(top, layer) : -1;
            return c;
        }
    }

    /// <summary>
    /// Armado de la seccion de la viga (sin la longitud): el estribo rectangular del alma,
    /// las capas de barras corridas de cada cara y los bastones colocados en sus capas.
    /// Geometria pura, compartida por la ventana (esquema) y el generador, para que lo
    /// que se ve sea lo que se crea.
    ///
    /// Reglas:
    ///  - un unico estribo cerrado en el alma, a "cover" de sus caras;
    ///  - la capa 1 de cada cara va pegada al estribo: sus dos barras extremas tangentes a
    ///    las ramas del estribo (esquinas) y las intermedias repartidas por igual entre
    ///    ellas, cada grupo con su diametro; las capas siguientes se apilan hacia dentro con
    ///    la separacion libre entre capas, repartidas en el mismo ancho;
    ///  - cada baston va apilado por dentro de las barras corridas de su cara (una capa nueva,
    ///    tangente a la mas interior o con el hueco que se pida) o intercalado en la capa 1,
    ///    en los huecos entre las corridas (de forma simetrica, con la separacion libre minima).
    /// </summary>
    public sealed class BeamPlan
    {
        public PlanOptions Opt;
        /// <summary>Hormigon del alma (referencia) y eje del estribo.</summary>
        public Rect Web, Line;
        public List<PlanLayer> Layers = new List<PlanLayer>();
        public List<PlanBar> Bars = new List<PlanBar>();
        public List<string> Warnings = new List<string>();
        public string Error;

        public double Cover => Opt.Cover;
        public double Ds => Opt.Ds;
        public IEnumerable<PlanLayer> LayersOf(bool top) => Layers.Where(l => l.Top == top);
        public int MainCount => Bars.Count(b => !b.IsBaston && !b.IsSide);
        public int SideCount => Bars.Count(b => b.IsSide);
        public int BastonCount => Bars.Count(b => b.IsBaston);
        /// <summary>Distancia de la cara superior / inferior a lo mas interior de su paquete de capas.</summary>
        public double TopInner => LayersOf(true).Select(l => l.Inner).DefaultIfEmpty(Cover + Ds).Max();
        public double BottomInner => LayersOf(false).Select(l => l.Inner).DefaultIfEmpty(Cover + Ds).Max();

        private static double ToMm(double ft) => Math.Round(ft * 304.8);
        private static string Mm(double ft) => ToMm(ft).ToString(CultureInfo.InvariantCulture);

        public string Describe() =>
            Error != null ? Error
            : MainCount + " barras corridas (" + LayersOf(true).Sum(l => l.Main) + " arriba, " + LayersOf(false).Sum(l => l.Main) + " abajo)" +
              (SideCount > 0 ? ", " + SideCount + " laterales" : "") + (BastonCount > 0 ? ", " + BastonCount + " de bastones" : "") + ", estribo " + Mm(Line.W + Ds) + " x " + Mm(Line.H + Ds);

        /// <summary>Resumen por capa: "S1 3, S2 2 | I1 4".</summary>
        public string DescribeLayers() =>
            string.Join(", ", LayersOf(true).Select(l => l.Name + " " + l.Bars.Count)) + " | " +
            string.Join(", ", LayersOf(false).Select(l => l.Name + " " + l.Bars.Count));

        /// <summary>Descripcion de una capa como en los planos: "2 de 3/4 + 1 de 5/8" (con o sin sus bastones).</summary>
        public static string DescribeLayer(PlanLayer l, bool withBastones = true)
        {
            var parts = new List<string>();
            foreach (var g in l.Bars.Where(b => !b.IsBaston).GroupBy(b => b.TypeName).OrderByDescending(g => g.First().Db))
                parts.Add(g.Count() + " " + g.Key);
            if (withBastones)
                foreach (var g in l.Bars.Where(b => b.IsBaston).GroupBy(b => b.Baston))
                    parts.Add("baston " + (g.Key + 1) + ": " + g.Count() + " " + g.First().TypeName);
            return string.Join(" + ", parts);
        }

        public static BeamPlan Build(PlanOptions o)
        {
            var plan = new BeamPlan { Opt = o };
            double tol = o.Tol;
            if (o.Web == null) { plan.Error = "la viga no tiene alma"; return plan; }
            plan.Web = o.Web.Clone();
            plan.Line = o.Web.Inset(o.Cover + 0.5 * o.Ds);
            if (plan.Line.W <= tol || plan.Line.H <= tol)
            {
                plan.Error = "el estribo no cabe en el alma de " + Mm(o.Web.W) + " x " + Mm(o.Web.H) + " mm con recubrimiento " + Mm(o.Cover) + " mm";
                return plan;
            }
            double inU1 = plan.Line.U1 + 0.5 * o.Ds, inU2 = plan.Line.U2 - 0.5 * o.Ds;   // caras interiores de las ramas
            if (inU2 - inU1 <= tol) { plan.Error = "el estribo no deja ancho libre para las barras"; return plan; }

            foreach (bool top in new[] { true, false })
            {
                FaceCfg fc = top ? o.Top : o.Bottom;
                var pending = new List<(int index, BastonCfg cfg)>();
                if (o.Bastones != null)
                    for (int i = 0; i < o.Bastones.Count; i++)
                        if (o.Bastones[i] != null && o.Bastones[i].IsTop == top) pending.Add((i, o.Bastones[i]));
                int configured = fc?.Layers?.Count ?? 0;
                double outer = o.Cover + o.Ds;

                for (int li = 0; li < configured; li++)
                {
                    LayerCfg lc = fc.Layers[li];
                    int cnt = o.CountFor(top, li + 1);
                    if (cnt < 0) cnt = lc.Count;
                    if (li == 0) cnt = Math.Max(2, cnt);
                    var layer = new PlanLayer { Top = top, Index = li + 1, Outer = outer };
                    plan.PlaceMain(layer, lc, cnt, inU1, inU2);
                    if (li == 0) plan.PlaceBastones(layer, pending, stacked: false, inU1, inU2);
                    if (layer.Bars.Count == 0) continue;
                    plan.Close(layer);
                    outer = layer.Inner + o.LayerClear;
                }

                // bastones apilados: pegados por dentro de la capa 1 (con su hueco), en el hueco que dejan
                // las barras de las demas capas a esa altura (las capas 2 y 3 suelen ir solo a los costados)
                PlanLayer first = plan.LayersOf(top).FirstOrDefault(l => l.Index == 1);
                double base0 = first?.Inner ?? (o.Cover + o.Ds);
                for (int k = 0; k < pending.Count;)
                {
                    (int index, BastonCfg cfg) = pending[k];
                    if (!cfg.Stacked) { k++; continue; }
                    double db = Opt_Dia(o, cfg.BarTypeName);
                    if (db <= 0) { pending.RemoveAt(k); continue; }
                    double outerB = base0 + Math.Max(0, cfg.GapMm) / 304.8;
                    // barras de la cara que ocupan esa franja de altura: no se puede poner el baston encima
                    var blockers = new PlanLayer { Top = top, Index = 0, Outer = outerB };
                    foreach (PlanBar b in plan.Bars.Where(b => b.Top == top && !b.IsSide && (!b.IsBaston || o.Overlap(index, b.Baston))))
                        if (b.FaceOffset + 0.5 * b.Db > outerB + tol && b.FaceOffset - 0.5 * b.Db < outerB + db - tol) blockers.Bars.Add(b);
                    List<double> us = plan.Fit(blockers, cfg.Count, db, inU1, inU2, true, out int missing);
                    int idx = plan.LayersOf(top).Select(l => l.Index).DefaultIfEmpty(0).Max() + 1;
                    var layer = new PlanLayer { Top = top, Index = idx, Outer = outerB };
                    if (us != null) foreach (double u in us) plan.Add(layer, u, db, cfg.BarTypeName, BarKind.Baston, index);
                    if (us == null || missing > 0)
                        plan.Warnings.Add("baston " + (index + 1) + " (" + cfg.Describe + "): no caben " + (us == null ? cfg.Count : missing) +
                                          " barra(s) apiladas bajo la capa 1 con la separacion libre minima");
                    layer.Missing = us == null ? cfg.Count : missing;
                    if (layer.Bars.Count > 0) plan.Close(layer);
                    pending.RemoveAt(k);
                }
            }

            // --- barras laterales: pares simetricos pegados a las ramas del estribo, repartidos en el canto libre ---
            {
                int pairs = o.SideOverride >= 0 ? o.SideOverride : (o.Sides?.Pairs ?? 0);
                double dbS = pairs > 0 && o.Sides != null ? o.Dia(o.Sides.BarTypeName) : 0;
                if (pairs > 0 && dbS > 0)
                {
                    double vTop = plan.Web.V2 - plan.TopInner, vBot = plan.Web.V1 + plan.BottomInner;
                    double freeH = vTop - vBot;
                    double need = pairs * dbS + (pairs + 1) * Math.Max(o.MinClear, dbS);
                    if (freeH < need - tol)
                        plan.Warnings.Add("laterales: " + pairs + " par(es) no caben en el canto libre de " + Mm(freeH) + " mm con la separacion libre minima");
                    for (int k = 1; k <= pairs; k++)
                    {
                        double v = vBot + freeH * k / (pairs + 1);
                        foreach (double u in new[] { inU1 + 0.5 * dbS, inU2 - 0.5 * dbS })
                        {
                            var sb = new PlanBar { U = u, FaceOffset = v - plan.Web.V1, Db = dbS, TypeName = o.Sides.BarTypeName, Top = false, Layer = k, Kind = BarKind.Side };
                            plan.Assign(sb);
                            plan.Bars.Add(sb);
                        }
                    }
                }
            }

            // --- comprobaciones ---
            double free = plan.Web.H - plan.TopInner - plan.BottomInner;
            if (plan.Layers.Count > 0 && free < o.LayerClear - tol)
                plan.Error = "las capas superiores e inferiores se solapan: el canto minimo del alma (" + Mm(plan.Web.H) +
                             " mm) no da para " + plan.LayersOf(true).Count() + " capa(s) arriba y " + plan.LayersOf(false).Count() +
                             " abajo (faltan " + Mm(o.LayerClear - free) + " mm)";
            foreach (PlanLayer l in plan.Layers)
            {
                List<PlanBar> sorted = l.Bars.OrderBy(b => b.U).ToList();
                for (int i = 0; i + 1 < sorted.Count; i++)
                {
                    PlanBar a = sorted[i], b = sorted[i + 1];
                    double clear = (b.U - a.U) - 0.5 * (a.Db + b.Db);
                    double min = Math.Max(o.MinClear, Math.Max(a.Db, b.Db));
                    if (clear < min - tol)
                    {
                        plan.Warnings.Add("capa " + l.Name + ": barras a " + Mm(Math.Max(0, clear)) + " mm libres (minimo " + Mm(min) + ")");
                        break;
                    }
                }
                if (l.Missing > 0) plan.Warnings.Add("capa " + l.Name + ": no caben " + l.Missing + " barra(s) de baston");
            }
            return plan;
        }

        private void Close(PlanLayer layer)
        {
            layer.Inner = layer.Bars.Count == 0 ? layer.Outer : layer.Bars.Max(b => b.FaceOffset + 0.5 * b.Db);
            Layers.Add(layer);
        }

        private PlanBar Add(PlanLayer layer, double u, double db, string type, BarKind kind, int baston = -1)
        {
            var bar = new PlanBar
            {
                U = u, FaceOffset = layer.Outer + 0.5 * db, Db = db, TypeName = type ?? "", Top = layer.Top,
                Layer = layer.Index, Kind = kind, Baston = baston
            };
            Assign(bar);
            layer.Bars.Add(bar);
            Bars.Add(bar);
            return bar;
        }

        /// <summary>Barras corridas de la capa: extremas tangentes a las ramas del estribo, intermedias repartidas por igual.</summary>
        private void PlaceMain(PlanLayer layer, LayerCfg lc, int cnt, double inU1, double inU2)
        {
            if (cnt <= 0) return;
            double dbC = Opt.Dia(lc.BarTypeName);
            double dbI = Opt.Dia(lc.IntermediateOrCorner);
            if (dbC <= 0) return;
            if (dbI <= 0) dbI = dbC;
            double uL = inU1 + 0.5 * dbC, uR = inU2 - 0.5 * dbC;
            if (uR - uL < -Opt.Tol) { Warnings.Add("capa " + layer.Name + ": las barras extremas no caben en el ancho del estribo"); }
            if (cnt == 1)
            {
                Add(layer, 0.5 * (uL + uR), dbC, lc.BarTypeName, BarKind.Corner);
            }
            else
            {
                for (int i = 0; i < cnt; i++)
                {
                    bool corner = i == 0 || i == cnt - 1;
                    double u = uL + (uR - uL) * i / (cnt - 1);
                    Add(layer, u, corner ? dbC : dbI, corner ? lc.BarTypeName : lc.IntermediateOrCorner, corner ? BarKind.Corner : BarKind.Intermediate);
                }
            }
            layer.Main = cnt;
        }

        /// <summary>
        /// Coloca en la capa los bastones pendientes de ese tipo (apilados o intercalados). Si
        /// no caben con la separacion libre minima se colocan los que quepan y se avisa.
        /// </summary>
        private void PlaceBastones(PlanLayer layer, List<(int index, BastonCfg cfg)> pending, bool stacked, double inU1, double inU2)
        {
            for (int k = 0; k < pending.Count;)
            {
                (int index, BastonCfg cfg) = pending[k];
                if (cfg.Stacked != stacked) { k++; continue; }
                double db = Opt.Dia(cfg.BarTypeName);
                if (db <= 0) { pending.RemoveAt(k); continue; }   // sin tipo de barra: no se dibuja (la ventana lo marca)
                // los bastones que no coinciden en longitud con este no le estorban: ocupan el mismo sitio en la seccion
                var visible = new PlanLayer { Top = layer.Top, Index = layer.Index, Outer = layer.Outer };
                visible.Bars.AddRange(layer.Bars.Where(b => !b.IsBaston || Opt.Overlap(index, b.Baston)));
                List<double> us = Fit(visible, cfg.Count, db, inU1, inU2, true, out int missing);
                if (us != null) foreach (double u in us) Add(layer, u, db, cfg.BarTypeName, BarKind.Baston, index);
                if (missing > 0 || us == null)
                    Warnings.Add("baston " + (index + 1) + " (" + cfg.Describe + "): no caben " + (us == null ? cfg.Count : missing) +
                                 " barra(s) " + (stacked ? "apiladas" : "intercaladas en la capa 1") + " con la separacion libre minima");
                layer.Missing += us == null ? cfg.Count : missing;
                pending.RemoveAt(k);
            }
        }

        /// <summary>
        /// Posiciones (u) de "count" barras de diametro "db" en la capa: en una capa vacia,
        /// repartidas de rama a rama del estribo (o centrada si es una); en una capa con
        /// barras, en los huecos entre ellas, de forma simetrica desde el centro. Null si no
        /// caben con la separacion libre minima (salvo "force", que coloca las que quepan y
        /// cuenta las que faltan en "missing").
        /// </summary>
        private static double Opt_Dia(PlanOptions o, string name) => o.Dia(name);

        /// <summary>Da a la barra su clave (orden entre las de su misma cara, capa, clase y baston) y aplica el tipo asignado a mano si lo hay.</summary>
        private void Assign(PlanBar bar)
        {
            int ordinal = Bars.Count(b => b.Top == bar.Top && b.Layer == bar.Layer && b.Kind == bar.Kind && b.Baston == bar.Baston);
            bar.Key = (bar.Top ? "S" : "I") + "|" + bar.Layer + "|" + (int)bar.Kind + "|" + bar.Baston + "|" + ordinal;
            string t = Opt.TypeOverride?.Invoke(bar.Key);
            if (string.IsNullOrEmpty(t)) return;
            double d = Opt.Dia(t);
            if (d <= 0) return;
            // el eje se mueve para que la barra siga tangente a la superficie sobre la que apoya
            bar.FaceOffset += 0.5 * (d - bar.Db);
            bar.Db = d;
            bar.TypeName = t;
            bar.Assigned = true;
        }

        private List<double> Fit(PlanLayer layer, int count, double db, double inU1, double inU2, bool force, out int missing)
        {
            missing = 0;
            double clear = Math.Max(Opt.MinClear, db);
            var result = new List<double>();
            if (layer.Bars.Count == 0)
            {
                double uL = inU1 + 0.5 * db, uR = inU2 - 0.5 * db;
                if (count == 1) { result.Add(0.5 * (uL + uR)); return result; }
                int cap = (int)Math.Floor((inU2 - inU1 - clear + 1e-9) / (db + clear));
                if (cap < count && !force) return null;   // con force se colocan igual y la comprobacion de separacion avisa
                for (int i = 0; i < count; i++) result.Add(uL + (uR - uL) * i / (count - 1));
                return result;
            }

            // huecos entre barras (y entre las ramas del estribo y la primera / ultima)
            List<PlanBar> sorted = layer.Bars.OrderBy(b => b.U).ToList();
            var gaps = new List<(double a, double b)>();
            gaps.Add((inU1, sorted[0].U - 0.5 * sorted[0].Db));
            for (int i = 0; i + 1 < sorted.Count; i++) gaps.Add((sorted[i].U + 0.5 * sorted[i].Db, sorted[i + 1].U - 0.5 * sorted[i + 1].Db));
            gaps.Add((sorted[sorted.Count - 1].U + 0.5 * sorted[sorted.Count - 1].Db, inU2));
            int n = gaps.Count;
            var cap2 = new int[n];
            var used = new int[n];
            for (int i = 0; i < n; i++) cap2[i] = Math.Max(0, (int)Math.Floor((gaps[i].b - gaps[i].a - clear + 1e-9) / (db + clear)));
            int total = cap2.Sum();
            if (total < count)
            {
                if (!force) return null;
                // se colocan igual, en los huecos mas anchos; la comprobacion de separacion libre avisa
                for (int i = 0; i < n; i++) cap2[i] = count;
            }

            // reparto simetrico: pares de huecos simetricos respecto al centro, el mas cercano primero; el central para la barra impar
            double center = 0.5 * (inU1 + inU2);
            double Dist(int i) => 0.5 * (gaps[i].a + gaps[i].b) - center;
            int remaining = count;
            while (remaining > 0)
            {
                int best = -1;
                if (remaining >= 2)
                {
                    double bestD = double.MaxValue;
                    for (int i = 0; i < n; i++)
                        for (int j = i + 1; j < n; j++)
                            if (cap2[i] > used[i] && cap2[j] > used[j] && Math.Abs(Dist(i) + Dist(j)) <= Opt.Tol && Math.Abs(Dist(i)) < bestD)
                            { bestD = Math.Abs(Dist(i)); best = i * n + j; }
                    if (best >= 0)
                    {
                        used[best / n]++; used[best % n]++;
                        remaining -= 2;
                        continue;
                    }
                }
                // hueco centrado con sitio; si no, el que mas capacidad libre tenga
                best = -1;
                for (int i = 0; i < n; i++)
                    if (cap2[i] > used[i] && Math.Abs(Dist(i)) <= Opt.Tol) { best = i; break; }
                if (best < 0)
                {
                    int bestFree = 0;
                    for (int i = 0; i < n; i++)
                        if (cap2[i] - used[i] > bestFree) { bestFree = cap2[i] - used[i]; best = i; }
                }
                if (best < 0) break;
                used[best]++;
                remaining--;
            }
            for (int i = 0; i < n; i++)
            {
                double len = gaps[i].b - gaps[i].a;
                for (int k = 1; k <= used[i]; k++) result.Add(gaps[i].a + len * k / (used[i] + 1));
            }
            return result;
        }

        /// <summary>
        /// Filas de barras iguales (misma cara, capa, baston, tipo y distancia a la cara)
        /// alineadas y equiespaciadas a lo largo de u: cada fila es un conjunto de Revit
        /// con "count" barras desde "first" cada "step". Las filas de una sola barra son
        /// conjuntos sencillos.
        /// </summary>
        public List<(PlanBar first, int count, double step)> ArrayRows(double tol)
        {
            var rows = new List<(PlanBar, int, double)>();
            var groups = Bars.GroupBy(b => (b.Top, b.Layer, b.Baston, b.TypeName, Math.Round(b.FaceOffset / tol)));
            foreach (var g in groups)
            {
                List<PlanBar> list = g.OrderBy(b => b.U).ToList();
                int i = 0;
                while (i < list.Count)
                {
                    if (i + 1 >= list.Count) { rows.Add((list[i], 1, 0)); i++; continue; }
                    double step = list[i + 1].U - list[i].U;
                    int j = i + 1;
                    while (j + 1 < list.Count && Math.Abs((list[j + 1].U - list[j].U) - step) <= tol) j++;
                    rows.Add((list[i], j - i + 1, step));
                    i = j + 1;
                }
            }
            return rows;
        }
    }

    /// <summary>
    /// Trayectoria (w, v) de una barra longitudinal a lo largo de la viga: sigue la cara
    /// de su lado en los tramos de canto variable y salva los escalones con una bayoneta
    /// a 45 grados dentro del lado de mas canto. Geometria pura.
    /// </summary>
    public static class BarPaths
    {
        /// <summary>
        /// Puntos (w, v) de la barra entre w0 y w1 (pueden salir de 0..L: fuera de la viga
        /// se mantiene la cota de la cara del extremo). "jogInset" es lo que la bayoneta se
        /// mete respecto al plano del escalon (recubrimiento + estribo + diametro).
        /// </summary>
        public static List<(double w, double v)> Path(BeamProfile prof, bool top, double faceOffset, double w0, double w1,
                                                      double jogInset, double tol, List<string> warnings, string name)
        {
            var pts = new List<(double w, double v)>();
            double L = prof.Length;
            double VAt(double w)
            {
                Rect r = prof.WebAt(Math.Max(0, Math.Min(L, w)));
                return top ? r.V2 - faceOffset : r.V1 + faceOffset;
            }
            pts.Add((w0, VAt(w0)));
            if (w0 < 0) pts.Add((0, VAt(0)));
            foreach (ProfileSegment seg in prof.Segments.Skip(1))
            {
                double wb = seg.W0;
                if (wb <= Math.Max(w0, 0) + tol || wb >= Math.Min(w1, L) - tol) continue;
                ProfileSegment prev = prof.Segments[seg.Index - 1];
                double vA = top ? prev.Web1.V2 - faceOffset : prev.Web1.V1 + faceOffset;
                double vB = top ? seg.Web0.V2 - faceOffset : seg.Web0.V1 + faceOffset;
                if (Math.Abs(vA - vB) <= tol) { pts.Add((wb, vB)); continue; }
                bool deepA = top ? vA > vB : vA < vB;
                double dv = Math.Abs(vA - vB);
                if (deepA)
                {
                    if (wb - jogInset - dv < prev.W0 + tol)
                        warnings?.Add(name + ": la bayoneta del escalon en w=" + Math.Round(wb * 304.8) + " mm no cabe en el tramo " + (prev.Index + 1));
                    pts.Add((wb - jogInset - dv, vA));
                    pts.Add((wb - jogInset, vB));
                }
                else
                {
                    if (wb + jogInset + dv > seg.W1 - tol)
                        warnings?.Add(name + ": la bayoneta del escalon en w=" + Math.Round(wb * 304.8) + " mm no cabe en el tramo " + (seg.Index + 1));
                    pts.Add((wb + jogInset, vA));
                    pts.Add((wb + jogInset + dv, vB));
                }
            }
            if (w1 > L) pts.Add((L, VAt(L)));
            pts.Add((w1, VAt(w1)));
            return Clean(pts, tol);
        }

        /// <summary>Quita puntos repetidos y vertices intermedios de tramos colineales.</summary>
        public static List<(double w, double v)> Clean(List<(double w, double v)> pts, double tol)
        {
            var list = new List<(double w, double v)>();
            foreach (var p in pts)
                if (list.Count == 0 || Math.Abs(list[list.Count - 1].w - p.w) > tol || Math.Abs(list[list.Count - 1].v - p.v) > tol) list.Add(p);
            bool changed = true;
            while (changed && list.Count > 2)
            {
                changed = false;
                for (int i = 1; i + 1 < list.Count; i++)
                {
                    var a = list[i - 1]; var b = list[i]; var c = list[i + 1];
                    double cross = (b.w - a.w) * (c.v - b.v) - (b.v - a.v) * (c.w - b.w);
                    double la = Math.Sqrt((b.w - a.w) * (b.w - a.w) + (b.v - a.v) * (b.v - a.v));
                    double lc = Math.Sqrt((c.w - b.w) * (c.w - b.w) + (c.v - b.v) * (c.v - b.v));
                    if (la > 1e-12 && lc > 1e-12 && Math.Abs(cross) / (la * lc) < 1e-6 &&
                        (b.w - a.w) * (c.w - b.w) + (b.v - a.v) * (c.v - b.v) > 0)
                    { list.RemoveAt(i); changed = true; break; }
                }
            }
            return list;
        }

        /// <summary>Cota v de la trayectoria en w (interpolando entre sus puntos; fuera de ella, la del extremo).</summary>
        public static double VAt(List<(double w, double v)> path, double w)
        {
            if (path == null || path.Count == 0) return 0;
            if (w <= path[0].w) return path[0].v;
            for (int i = 0; i + 1 < path.Count; i++)
            {
                var a = path[i]; var b = path[i + 1];
                if (w <= b.w)
                {
                    double span = b.w - a.w;
                    return span <= 1e-12 ? b.v : a.v + (b.v - a.v) * (w - a.w) / span;
                }
            }
            return path[path.Count - 1].v;
        }

        /// <summary>Trozo de la trayectoria entre wa y wb (un trozo de barra empalmada), con puntos interpolados en los cortes.</summary>
        public static List<(double w, double v)> Clip(List<(double w, double v)> path, double wa, double wb, double tol)
        {
            var pts = new List<(double w, double v)> { (wa, VAt(path, wa)) };
            foreach (var p in path)
                if (p.w > wa + tol && p.w < wb - tol) pts.Add(p);
            pts.Add((wb, VAt(path, wb)));
            return Clean(pts, tol);
        }

        /// <summary>
        /// Bayoneta del empalme: la trayectoria va desplazada "dv" en v desde su inicio hasta
        /// wEnd (donde acaba el solape, pegada a la barra con la que empalma) y vuelve a su
        /// linea en wEnd + jog.
        /// </summary>
        public static List<(double w, double v)> Jog(List<(double w, double v)> path, double dv, double wEnd, double jog, double tol)
        {
            if (path == null || path.Count == 0 || Math.Abs(dv) <= tol) return path;
            double wBack = wEnd + Math.Max(jog, tol);
            double first = path[0].w, last = path[path.Count - 1].w;
            var knots = path.Select(p => p.w).ToList();
            knots.Add(wEnd);
            knots.Add(wBack);
            var pts = new List<(double w, double v)>();
            foreach (double w in knots.Where(k => k >= first - tol && k <= last + tol).OrderBy(k => k))
            {
                double f = w <= wEnd ? 1 : w >= wBack ? 0 : (wBack - w) / (wBack - wEnd);
                pts.Add((w, VAt(path, w) + dv * f));
            }
            return Clean(pts, tol);
        }

        /// <summary>Longitud desarrollada de la trayectoria (pies).</summary>
        public static double Length(List<(double w, double v)> pts)
        {
            double l = 0;
            for (int i = 0; i + 1 < pts.Count; i++)
                l += Math.Sqrt((pts[i + 1].w - pts[i].w) * (pts[i + 1].w - pts[i].w) + (pts[i + 1].v - pts[i].v) * (pts[i + 1].v - pts[i].v));
            return l;
        }
    }
}
