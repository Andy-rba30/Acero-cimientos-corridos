using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace FootingRebar
{
    /// <summary>Un conjunto (elemento Rebar) creado para una viga.</summary>
    public sealed class CreatedSet
    {
        public ElementId Id;
        public string Name;
        /// <summary>Radio nominal de la barra (pies).</summary>
        public double Radius;
        /// <summary>Barra longitudinal: se comprueba solo el tramo dentro de la longitud de la viga (puede sobresalir a proposito en los apoyos).</summary>
        public bool Longitudinal;
    }

    /// <summary>Resultado del armado de un elemento.</summary>
    public sealed class BuildResult
    {
        public List<CreatedSet> Created = new List<CreatedSet>();
        /// <summary>Barras que quedarian fuera del hormigon. Si hay alguna, el elemento entero se deshace.</summary>
        public List<string> Rejected = new List<string>();
        /// <summary>Conjuntos que Revit no pudo crear.</summary>
        public List<string> Failed = new List<string>();
        public List<string> Warnings = new List<string>();
        public int Bars, BastonBars, StirrupSets, Stirrups;
        /// <summary>Empalmes por traslape creados (uno por cada barra corrida partida por la longitud comercial).</summary>
        public int Splices;
        public string Summary => Bars + " barras corridas" + (Splices > 0 ? " con " + Splices + " empalmes" : "") +
                                 (BastonBars > 0 ? ", " + BastonBars + " de bastones" : "") + ", " +
                                 Stirrups + " estribos en " + StirrupSets + " conjuntos";
        public bool Safe => Rejected.Count == 0;
    }

    /// <summary>Un baston resuelto para una viga concreta: su tramo (w) y sus barras en la seccion.</summary>
    public sealed class BastonRange
    {
        public int Index;
        public BastonCfg Cfg;
        public double W0, W1;
        public string Label;
    }

    public static class RebarGenerator
    {
        private static double Mm(double mm) => BeamSection.Mm(mm);
        private static double ToMm(double ft) => BeamSection.ToMm(ft);
        private const double MinSeg = 0.003;   // ~1 mm en pies
        /// <summary>Longitud de barra que se tolera fuera del solido al comprobar (pies, ~1 mm).</summary>
        private const double InsideTol = 0.0033;

        private sealed class Ctx
        {
            public Document Doc;
            public HostAnalysis Item;
            public BeamSection S;
            public AppConfig Cfg;
            public BuildResult Result;
            public BeamPlan Plan;
            public List<StirrupRun> Runs;
            public double Tol;
            public bool HookLeft, HookChecked;
            public ElementId Hook = ElementId.InvalidElementId;
        }

        // =================================================================
        // Armado de una viga
        // =================================================================
        public static BuildResult Build(Document doc, HostAnalysis item, AppConfig cfg)
        {
            var c = new Ctx
            {
                Doc = doc, Item = item, S = item.Section, Cfg = cfg, Result = new BuildResult(),
                Tol = Mm(cfg.PrismCheckToleranceMm), HookLeft = cfg.HookLeft
            };

            // tipos de barra: todos los que se usan tienen que existir
            var types = new Dictionary<string, RebarBarType>(StringComparer.OrdinalIgnoreCase);
            RebarBarType Type(string name, string use)
            {
                if (!types.TryGetValue(name ?? "", out RebarBarType bt))
                {
                    bt = FindBarType(doc, name, use);
                    types[name ?? ""] = bt;
                }
                return bt;
            }
            foreach (bool top in new[] { true, false })
                for (int i = 0; i < cfg.Face(top).Layers.Count; i++)
                {
                    LayerCfg l = cfg.Face(top).Layers[i];
                    int cnt = item.Own(top, i + 1);
                    if (cnt < 0) cnt = l.Count;
                    if (i == 0 || cnt > 0)
                    {
                        Type(l.BarTypeName, "barras " + (top ? "superiores" : "inferiores") + " capa " + (i + 1));
                        Type(l.IntermediateOrCorner, "barras intermedias " + (top ? "superiores" : "inferiores") + " capa " + (i + 1));
                    }
                }
            for (int i = 0; i < cfg.Bastones.Count; i++) Type(cfg.Bastones[i].BarTypeName, "baston " + (i + 1));
            foreach (string t in item.BarTypeOverrides.Values.Distinct(StringComparer.OrdinalIgnoreCase)) Type(t, "barras con tipo asignado");
            int sidePairs = item.OwnSide() >= 0 ? item.OwnSide() : cfg.SideBars.Pairs;
            if (sidePairs > 0) Type(cfg.SideBars.BarTypeName, "barras laterales");
            RebarBarType btStirrup = FindBarType(doc, cfg.Stirrups.BarTypeName, "estribos");
            c.Hook = FindHookType(doc, cfg.Stirrups.HookTypeName);

            double Dia(string name) => types.TryGetValue(name ?? "", out RebarBarType bt) ? bt.BarNominalDiameter : 0;
            c.Plan = PlanFor(item, cfg, Dia, btStirrup.BarNominalDiameter, 0);
            if (c.Plan.Error != null) throw new InvalidOperationException(c.Plan.Error);
            c.Result.Warnings.AddRange(c.Plan.Warnings);

            c.Runs = RunsFor(item, cfg, out string warn);
            if (warn != null) c.Result.Warnings.Add(warn);
            if (c.Runs.Count == 0) throw new InvalidOperationException("la distribucion de estribos no produce ningun estribo");

            List<BastonRange> bastones = BastonRanges(item.Section, cfg, out List<string> bErrors);
            if (bErrors.Count > 0) throw new InvalidOperationException(string.Join(" | ", bErrors));

            Longitudinals(c, types, bastones);
            if (!c.Result.Safe) return c.Result;
            Stirrups(c, btStirrup);
            return c.Result;
        }

        /// <summary>Armado de la seccion con esta configuracion (lo mismo que dibuja la ventana).</summary>
        public static BeamPlan PlanFor(HostAnalysis item, AppConfig cfg, Func<string, double> diameter, double dsFt, double fallbackDb)
        {
            BeamSection s = item.Section;
            var o = new PlanOptions
            {
                Web = s.Profile.MinWeb, Cover = Mm(cfg.CoverMm), Ds = dsFt,
                LayerClear = Mm(cfg.Longitudinal.LayerClearMm), MinClear = Mm(cfg.Longitudinal.MinClearMm),
                Top = cfg.TopBars, Bottom = cfg.BottomBars, Bastones = cfg.Bastones,
                Diameter = diameter, CountOverride = (top, layer) => item.Own(top, layer),
                Sides = cfg.SideBars, SideOverride = item.OwnSide(),
                TypeOverride = key => item.BarTypeOverrides.TryGetValue(key, out string t) ? t : null,
                FallbackDb = fallbackDb, Tol = Mm(cfg.PrismCheckToleranceMm)
            };
            List<BastonRange> ranges = BastonRanges(s, cfg, out _);
            o.BastonsOverlap = (i, j) => ranges.Any(a => a.Index == i && ranges.Any(b => b.Index == j && a.W0 < b.W1 - 1e-9 && b.W0 < a.W1 - 1e-9));
            return BeamPlan.Build(o);
        }

        /// <summary>Tramos de estribos de esta viga con esta configuracion. Lanza si la distribucion no se entiende.</summary>
        public static List<StirrupRun> RunsFor(HostAnalysis item, AppConfig cfg, out string warning)
        {
            List<StirrupGroup> groups = StirrupLayout.Parse(item.Distribution(cfg), out string err);
            if (groups == null) throw new InvalidOperationException("distribucion de estribos \"" + item.Distribution(cfg) + "\": " + err);
            return StirrupLayout.Runs(item.Section.Length, Mm(cfg.Stirrups.StartOffsetMm), Mm(cfg.Stirrups.EndOffsetMm),
                                      groups, cfg.Stirrups.Symmetric, out warning);
        }

        /// <summary>
        /// Tramo (w0, w1) de cada baston en esta viga: desde el apoyo (con su anclaje mas alla
        /// de la cara, o desde el recubrimiento del extremo), centrado en el vano, o el tramo
        /// escrito. "Ambos extremos" produce dos tramos. Los errores de longitud van en "errors".
        /// </summary>
        public static List<BastonRange> BastonRanges(BeamSection s, AppConfig cfg, out List<string> errors)
        {
            errors = new List<string>();
            var list = new List<BastonRange>();
            double L = s.Length;
            double endCover = Mm(cfg.Longitudinal.EndCoverMm);
            for (int i = 0; i < cfg.Bastones.Count; i++)
            {
                BastonCfg b = cfg.Bastones[i];
                string name = "baston " + (i + 1) + " (" + b.Describe + ")";
                int pos = b.PositionIndex;
                if (pos == 3 && (b.FromMm > 0 || b.ToMm > 0))
                {
                    // centro con longitudes distintas hacia cada lado
                    double a = 0.5 * L - Mm(b.FromMm), z = 0.5 * L + Mm(b.ToMm);
                    if (a < 0 || z > L + 1e-9) { errors.Add(name + ": las longitudes hacia inicio / fin se salen de la viga"); continue; }
                    list.Add(new BastonRange { Index = i, Cfg = b, W0 = a, W1 = z, Label = "centro " + b.FromMm.ToString("0") + "+" + b.ToMm.ToString("0") });
                    continue;
                }
                if (!StirrupLayout.TryLength(b.Length, L * BeamSection.MmPerFt, out double lenMm, out string lerr)) { errors.Add(name + ": " + lerr); continue; }
                double len = Mm(lenMm);
                if (pos == 3)
                {
                    if (len > L + 1e-9) { errors.Add(name + ": la longitud (" + Math.Round(lenMm) + " mm) es mayor que la viga"); continue; }
                    list.Add(new BastonRange { Index = i, Cfg = b, W0 = 0.5 * (L - len), W1 = 0.5 * (L + len), Label = "centro " + Math.Round(lenMm) });
                    continue;
                }
                double anchor = Mm(b.AnchorMm);
                if (len > L + 1e-9) { errors.Add(name + ": la longitud (" + Math.Round(lenMm) + " mm) es mayor que la viga"); continue; }
                if (pos == 0 || pos == 2)
                    list.Add(new BastonRange { Index = i, Cfg = b, W0 = anchor > 0 ? -anchor : endCover, W1 = len, Label = "inicio " + Math.Round(lenMm) });
                if (pos == 1 || pos == 2)
                    list.Add(new BastonRange { Index = i, Cfg = b, W0 = L - len, W1 = anchor > 0 ? L + anchor : L - endCover, Label = "fin " + Math.Round(lenMm) });
            }
            foreach (BastonRange r in list)
                if (r.W1 - r.W0 < Mm(50)) errors.Add("baston " + (r.Index + 1) + " (" + r.Cfg.Describe + "): tramo demasiado corto");
            return list;
        }

        /// <summary>Lo que la bayoneta de un escalon se mete respecto al plano del escalon.</summary>
        public static double JogInset(AppConfig cfg, double ds, double db) => Mm(cfg.CoverMm) + ds + db;

        /// <summary>Tramo (w0, w1) de las barras corridas: con prolongacion sobresalen de la cara; sin ella terminan en el recubrimiento del extremo.</summary>
        public static (double w0, double w1) MainRange(BeamSection s, AppConfig cfg)
        {
            LongitudinalCfg L = cfg.Longitudinal;
            return (L.StartExtensionMm > 0 ? -Mm(L.StartExtensionMm) : Mm(L.EndCoverMm),
                    L.EndExtensionMm > 0 ? s.Length + Mm(L.EndExtensionMm) : s.Length - Mm(L.EndCoverMm));
        }

        /// <summary>Tramos del recorrido (cotas w): los elementos de la cadena entre columnas o esquinas; uno solo si no hay cadena.</summary>
        public static List<(double w0, double w1)> Spans(BeamSection s)
        {
            var spans = new List<(double w0, double w1)>();
            if (s.Chain != null && s.Chain.Segs.Count > 0)
                foreach (ChainSeg g in s.Chain.Segs) spans.Add((g.W0, g.W1));
            else spans.Add((0, s.Length));
            return spans;
        }

        /// <summary>
        /// Empalmes de una fila de barras corridas de este recorrido: longitud de empalme
        /// segun su diametro y su posicion (ACI 318-19; factor 1.3 si tiene mas de 300 mm
        /// de hormigon fresco debajo), trozos y centros, con cada empalme en la zona de
        /// alguno de los tramos del recorrido. Compartido por la ventana y el generador,
        /// para que lo que se dibuja sea lo que se crea. Las laterales se empalman en el tercio central.
        /// </summary>
        public static SplicedBar SpliceFor(BeamSection s, AppConfig cfg, PlanBar bar, double w0, double w1, double tol)
        {
            SpliceCfg sc = cfg.Splices ?? new SpliceCfg();
            BeamProfile prof = s.Profile;
            double below = bar.Top ? prof.MaxDepth - bar.FaceOffset : bar.FaceOffset;
            bool topBar = below > Mm(300);
            double lap = Mm(SpliceLayout.LapLengthMm(sc, bar.Db * BeamSection.MmPerFt, topBar));
            bool center = bar.IsSide || sc.CenterZone(bar.Top);
            return SpliceLayout.Plan(sc, lap, bar.Db, Spans(s), prof.MaxDepth, w0, w1, center, bar.Label, tol);
        }

        /// <summary>
        /// Trayectoria de cada trozo de una barra empalmada: el primero sigue la linea de la
        /// barra; cada uno de los siguientes empieza pegado por dentro al anterior (desplazado
        /// un diametro hacia el interior de la seccion) y vuelve a la linea con una bayoneta
        /// al acabar el solape. "midV" es la cota media del alma (hacia donde se meten las laterales).
        /// </summary>
        public static List<List<(double w, double v)>> PiecePaths(List<(double w, double v)> path, SplicedBar sb, PlanBar bar, double midV, double tol)
        {
            var list = new List<List<(double w, double v)>>();
            if (sb == null || !sb.Spliced) { list.Add(path); return list; }
            double dir = bar.IsSide ? (BarPaths.VAt(path, 0.5 * (path[0].w + path[path.Count - 1].w)) > midV ? -1 : 1) : bar.Top ? -1 : 1;
            for (int k = 0; k < sb.Pieces.Count; k++)
            {
                (double a, double z) = sb.Pieces[k];
                List<(double w, double v)> piece = BarPaths.Clip(path, a, z, tol);
                if (k > 0) piece = BarPaths.Jog(piece, dir * bar.Db, sb.Centers[k - 1] + 0.5 * sb.Lap, sb.Jog, tol);
                list.Add(piece);
            }
            return list;
        }

        /// <summary>
        /// Resumen de los empalmes de las barras corridas de este recorrido ("empalmes:
        /// superiores 1 x 1400 mm (3/4) en el tercio central; inferiores ...") y sus avisos;
        /// null si ninguna barra necesita empalme.
        /// </summary>
        public static string DescribeSplices(BeamSection s, AppConfig cfg, BeamPlan plan, out List<string> warnings)
        {
            warnings = new List<string>();
            if (cfg.Splices == null || !cfg.Splices.Enabled || plan == null || plan.Error != null) return null;
            double tol = Mm(cfg.PrismCheckToleranceMm);
            (double w0, double w1) = MainRange(s, cfg);
            var parts = new List<string>();
            foreach (var face in plan.Bars.Where(b => !b.IsBaston).GroupBy(b => b.IsSide ? 2 : b.Top ? 0 : 1).OrderBy(g => g.Key))
            {
                var laps = new List<string>();
                foreach (PlanBar bar in face.GroupBy(b => (b.TypeName, Math.Round(b.FaceOffset / tol))).Select(g => g.First()))
                {
                    SplicedBar sb = SpliceFor(s, cfg, bar, w0, w1, tol);
                    foreach (string w in sb.Warnings) if (!warnings.Contains(w)) warnings.Add(w);
                    if (!sb.Spliced) continue;
                    string t = sb.Centers.Count + " x " + ToMm(sb.Lap) + " mm (" + bar.TypeName + ")";
                    if (!laps.Contains(t)) laps.Add(t);
                }
                if (laps.Count == 0) continue;
                bool center = face.Key == 2 || cfg.Splices.CenterZone(face.Key == 0);
                parts.Add((face.Key == 0 ? "superiores " : face.Key == 1 ? "inferiores " : "laterales ") + string.Join(" + ", laps) +
                          (center ? " en el tercio central de los tramos" : " cerca de los apoyos de los tramos"));
            }
            return parts.Count == 0 ? null : "empalmes: " + string.Join("; ", parts);
        }

        // -----------------------------------------------------------------
        // Longitudinales: cada fila equiespaciada = un conjunto con array a lo largo de u
        // -----------------------------------------------------------------
        private static void Longitudinals(Ctx c, Dictionary<string, RebarBarType> types, List<BastonRange> bastones)
        {
            BeamSection s = c.S;
            LongitudinalCfg L = c.Cfg.Longitudinal;
            double ext0 = Mm(L.StartExtensionMm), ext1 = Mm(L.EndExtensionMm);
            double endCover = Mm(L.EndCoverMm);
            double leg = Mm(L.LegMm);
            bool warnedLegs = false;

            foreach ((PlanBar first, int count, double step) in c.Plan.ArrayRows(c.Tol))
            {
                RebarBarType bt = types[first.TypeName];
                double db = bt.BarNominalDiameter;
                var ranges = new List<(double w0, double w1, string label, bool legStart, bool legEnd, string face)>();
                if (first.IsBaston)
                {
                    foreach (BastonRange r in bastones.Where(r => r.Index == first.Baston))
                        ranges.Add((r.W0, r.W1, r.Label, false, false, "baston"));
                }
                else
                {
                    ranges.Add((ext0 > 0 ? -ext0 : endCover, ext1 > 0 ? s.Length + ext1 : s.Length - endCover, "",
                                leg > 0 && L.LegAtStart && !first.IsSide, leg > 0 && L.LegAtEnd && !first.IsSide, first.IsSide ? "lateral" : first.Top ? "superior" : "inferior"));
                }

                foreach ((double w0, double w1, string label, bool legStart, bool legEnd, string face) in ranges)
                {
                    if (w1 - w0 < MinSeg) { c.Result.Rejected.Add(first.Label + ": sin longitud"); return; }
                    List<(double w, double v)> full = BarPaths.Path(s.Profile, first.Top, first.FaceOffset, w0, w1,
                                                                    JogInset(c.Cfg, c.Plan.Ds, db), c.Tol, c.Result.Warnings, first.Label);
                    // empalmes por longitud comercial: las corridas mas largas se parten en trozos solapados
                    SplicedBar sb = first.IsBaston ? null : SpliceFor(s, c.Cfg, first, w0, w1, c.Tol);
                    if (sb != null)
                        foreach (string w in sb.Warnings) if (!c.Result.Warnings.Contains(w)) c.Result.Warnings.Add(w);
                    if (first.IsBaston && c.Cfg.Splices != null && c.Cfg.Splices.Enabled && w1 - w0 > Mm(c.Cfg.Splices.CommercialLengthMm) + c.Tol)
                    {
                        string w = first.Label + " " + label + ": mide " + ToMm(w1 - w0) + " mm, mas que la barra comercial (" +
                                   c.Cfg.Splices.CommercialLengthMm.ToString("0") + " mm); los bastones no se empalman";
                        if (!c.Result.Warnings.Contains(w)) c.Result.Warnings.Add(w);
                    }
                    List<List<(double w, double v)>> pieces = PiecePaths(full, sb, first, c.Plan.Web.CV, c.Tol);
                    // patilla en el inicio: las superiores bajan, las inferiores suben (solo en el primer y ultimo trozo)
                    double legDir = first.Top ? -1 : 1;
                    bool corners = s.Chain != null && s.Chain.HasCorners;
                    // en un recorrido con esquinas cada barra dobla en planta: es plana solo en horizontal, asi que
                    // va sin patillas, una a una (no como array) y con la normal vertical
                    bool useLegStart = legStart, useLegEnd = legEnd;
                    if (corners && (legStart || legEnd) && !warnedLegs)
                    {
                        c.Result.Warnings.Add("las patillas se omiten en las barras que doblan en las esquinas del recorrido");
                        warnedLegs = true;
                    }
                    if (corners) { useLegStart = false; useLegEnd = false; }
                    int singles = corners ? count : 1;
                    for (int k = 0; k < pieces.Count; k++)
                    {
                        List<(double w, double v)> path = pieces[k];
                        string name = first.Label + (label.Length > 0 ? " " + label : "") +
                                      (pieces.Count > 1 ? " tramo " + (k + 1) + "/" + pieces.Count : "") +
                                      (count > 1 ? " (" + count + " barras cada " + ToMm(step) + " mm)" : "") + " u=" + ToMm(first.U);
                        for (int kbar = 0; kbar < singles; kbar++)
                        {
                            double u = first.U + kbar * step;
                            var curves = new List<Curve>();
                            List<XYZ> pts = corners ? s.Chain.Polyline(u, path)
                                : path.Select(q => s.World(u, q.v, q.w)).ToList();
                            if (useLegStart && k == 0) AddLine(curves, s.World(u, path[0].v + legDir * leg, path[0].w), pts[0]);
                            for (int i = 0; i + 1 < pts.Count; i++) AddLine(curves, pts[i], pts[i + 1]);
                            if (useLegEnd && k == pieces.Count - 1)
                            {
                                var e = path[path.Count - 1];
                                AddLine(curves, pts[pts.Count - 1], s.World(u, e.v + legDir * leg, e.w));
                            }
                            if (curves.Count == 0) { c.Result.Rejected.Add(name + ": sin geometria"); return; }
                            XYZ normal = corners ? XYZ.BasisZ : s.DirU;
                            bool ok = Place(c, name + (corners && count > 1 ? " barra " + (kbar + 1) : ""), bt, RebarStyle.Standard, ElementId.InvalidElementId, true,
                                            normal, curves, corners ? 1 : count, step, longitudinal: true, face: face, host: s.Host);
                            if (!ok) return;
                        }
                        if (first.IsBaston) c.Result.BastonBars += count; else c.Result.Bars += count;
                    }
                    if (sb != null && sb.Spliced) c.Result.Splices += count * sb.Centers.Count;
                }
            }
        }

        // -----------------------------------------------------------------
        // Estribos: un conjunto por tramo de la distribucion y seccion constante
        // -----------------------------------------------------------------
        private static void Stirrups(Ctx c, RebarBarType bt)
        {
            BeamSection s = c.S;
            double inset = Mm(c.Cfg.CoverMm) + 0.5 * bt.BarNominalDiameter;
            foreach (StirrupRun run in c.Runs)
            {
                // estaciones consecutivas con el mismo rectangulo = un array; en los tramos de
                // canto variable cada estribo es distinto y va solo
                var group = new List<double>();
                Rect groupRect = null;
                void Flush()
                {
                    if (group.Count == 0) return;
                    if (!PlaceStirrup(c, bt, groupRect, group[0], group.Count, run.Spacing, run.Label)) throw new StopException();
                    group.Clear();
                    groupRect = null;
                }
                try
                {
                    ChainSeg groupSeg = null;
                    foreach (double w in run.Stations())
                    {
                        Rect r = s.Profile.WebAt(w).Inset(inset);
                        ChainSeg seg = s.Chain?.SegAt(w);
                        if (groupRect != null && SameRect(groupRect, r, c.Tol) && ReferenceEquals(seg, groupSeg)) { group.Add(w); continue; }
                        Flush();
                        groupRect = r;
                        groupSeg = seg;
                        group.Add(w);
                    }
                    Flush();
                }
                catch (StopException) { return; }
            }
        }

        private sealed class StopException : Exception { }

        private static bool SameRect(Rect a, Rect b, double tol) =>
            Math.Abs(a.U1 - b.U1) <= tol && Math.Abs(a.U2 - b.U2) <= tol && Math.Abs(a.V1 - b.V1) <= tol && Math.Abs(a.V2 - b.V2) <= tol;

        private static bool PlaceStirrup(Ctx c, RebarBarType bt, Rect r, double w, int count, double spacing, string label)
        {
            BeamSection s = c.S;
            if (r.W <= MinSeg || r.H <= MinSeg) { c.Result.Rejected.Add("estribo en w=" + ToMm(w) + " mm: el alma no tiene canto"); return false; }
            // en un recorrido, el estribo va en el sistema del tramo que lo contiene (y se aloja en su elemento)
            ChainSeg seg = s.Chain?.SegAt(w);
            BeamSection local = seg?.Section ?? s;
            double wl = seg != null ? w - seg.W0 : w;
            // antihorario visto desde el inicio, empezando y acabando en la esquina superior izquierda (ahi van los ganchos)
            XYZ p1 = local.World(r.U1, r.V2, wl), p2 = local.World(r.U1, r.V1, wl);
            XYZ p3 = local.World(r.U2, r.V1, wl), p4 = local.World(r.U2, r.V2, wl);
            var curves = new List<Curve>();
            AddLine(curves, p1, p2); AddLine(curves, p2, p3); AddLine(curves, p3, p4); AddLine(curves, p4, p1);
            string name = "estribo " + label + " w=" + ToMm(w) + (count > 1 ? " (" + count + " cada " + ToMm(spacing) + " mm)" : "") +
                          " " + ToMm(r.W + bt.BarNominalDiameter) + "x" + ToMm(r.H + bt.BarNominalDiameter);
            bool ok = Place(c, name, bt, RebarStyle.StirrupTie, c.Hook, c.HookLeft, local.DirW, curves, count, spacing,
                            longitudinal: false, face: "estribo", host: local.Host,
                            checkHooks: c.Hook != ElementId.InvalidElementId && !c.HookChecked,
                            flip: () => c.HookLeft = !c.HookLeft);
            if (c.Hook != ElementId.InvalidElementId) c.HookChecked = true;
            if (!ok) return false;
            c.Result.StirrupSets++;
            c.Result.Stirrups += count;
            return true;
        }

        // =================================================================
        // Colocacion con red de seguridad
        // =================================================================

        /// <summary>
        /// Comprueba que la barra (y todas las posiciones del array) queda dentro del
        /// hormigon y solo entonces la crea. Con "checkHooks", tras crearla lee su geometria
        /// real (ganchos incluidos); si los ganchos asoman, la borra, invierte la orientacion
        /// (flip) y la vuelve a crear. False si algo se rechazo.
        /// </summary>
        private static bool Place(Ctx c, string name, RebarBarType bt, RebarStyle style, ElementId hook, bool hookLeft,
                                  XYZ normal, List<Curve> curves, int count, double spacing, bool longitudinal, string face,
                                  bool checkHooks = false, Action flip = null, Element host = null)
        {
            host = host ?? c.S.Host;
            normal = normal.Normalize();
            bool array = count >= 2 && spacing > MinSeg;
            double r = bt.BarNominalDiameter * 0.5;

            // --- RED DE SEGURIDAD (1): geometria planificada, antes de crear nada ---
            for (int k = 0; k < (array ? count : 1); k++)
            {
                IList<Curve> moved = curves;
                if (k > 0)
                {
                    Transform t = Transform.CreateTranslation(normal * (k * spacing));
                    moved = curves.Select(cv => cv.CreateTransformed(t)).ToList();
                }
                if (!BarInside(c.S, moved, r, longitudinal, out string why))
                {
                    c.Result.Rejected.Add(name + (k > 0 ? " (posicion " + (k + 1) + " del array)" : "") + ": " + why);
                    return false;
                }
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                Rebar rb = Create(c.Doc, host, bt, style, hook, hookLeft, normal, curves, out string err);
                if (rb == null) { c.Result.Failed.Add(name + ": Revit no pudo crear la barra (" + err + ")"); return true; }

                if (array) rb.GetShapeDrivenAccessor().SetLayoutAsFixedNumber(count, (count - 1) * spacing, true, true, true);
                else rb.GetShapeDrivenAccessor().SetLayoutAsSingle();

                if (checkHooks && flip != null)
                {
                    // RED DE SEGURIDAD (1b): los ganchos solo existen en la geometria real
                    c.Doc.Regenerate();
                    if (!RealInside(c.S, rb, r, longitudinal, out string why))
                    {
                        c.Doc.Delete(rb.Id);
                        if (attempt == 0)
                        {
                            flip();
                            hookLeft = !hookLeft;
                            c.Result.Warnings.Add(name + ": los ganchos quedaban fuera del hormigon, se ha invertido su orientacion");
                            continue;
                        }
                        c.Result.Rejected.Add(name + ": " + why + " (con las dos orientaciones de gancho)");
                        return false;
                    }
                }

                Finish(c.Doc, rb, c.Item.Partition(c.Cfg, SetName(name), face));
                c.Result.Created.Add(new CreatedSet { Id = rb.Id, Name = name, Radius = r, Longitudinal = longitudinal });
                return true;
            }
            return false;
        }

        /// <summary>
        /// RED DE SEGURIDAD (2): tras crear y regenerar, se lee la geometria REAL de cada
        /// barra de cada conjunto tal y como la ha colocado Revit (radios de doblado, ganchos
        /// y todas las posiciones del array) y se comprueba contra el solido. Las
        /// longitudinales se comprueban solo dentro de la longitud de la viga (pueden
        /// sobresalir a proposito en los apoyos).
        /// </summary>
        public static void VerifyCreated(Document doc, BeamSection s, BuildResult res)
        {
            foreach (CreatedSet cs in res.Created)
            {
                var rb = doc.GetElement(cs.Id) as Rebar;
                if (rb == null) { res.Rejected.Add(cs.Name + ": el conjunto no existe tras regenerar"); continue; }
                if (!RealInside(s, rb, cs.Radius, cs.Longitudinal, out string why)) res.Rejected.Add(cs.Name + ": " + why);
            }
        }

        private static bool RealInside(BeamSection s, Rebar rb, double r, bool longitudinal, out string why)
        {
            why = null;
            int n;
            try { n = rb.NumberOfBarPositions; }
            catch (Exception ex) { why = "no se pudo leer el conjunto (" + ex.Message + ")"; return false; }
            for (int k = 0; k < n; k++)
            {
                IList<Curve> cl;
                try
                {
                    if (!rb.DoesBarExistAtPosition(k)) continue;
                    cl = rb.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, k);
                }
                catch (Exception ex) { why = "barra " + (k + 1) + " de " + n + ": no se pudo leer su geometria (" + ex.Message + ")"; return false; }
                if (cl == null || cl.Count == 0) { why = "barra " + (k + 1) + " de " + n + ": sin geometria"; return false; }
                if (!BarInside(s, cl, r, longitudinal, out string w)) { why = "barra " + (k + 1) + " de " + n + ": " + w; return false; }
            }
            return true;
        }

        /// <summary>
        /// True si toda la barra queda dentro del solido. Ademas del eje se comprueban fibras
        /// extremas (eje desplazado +-r en cada direccion local; en las longitudinales solo en
        /// el plano de la seccion), asi una barra tangente a una cara o con medio diametro
        /// fuera tambien falla. Las longitudinales se recortan a la longitud de la viga.
        /// </summary>
        private static bool BarInside(BeamSection s, IList<Curve> curves, double r, bool longitudinal, out string why)
        {
            why = null;
            List<Solid> solids = s.AllSolids();
            IEnumerable<Curve> toCheck = longitudinal
                ? curves.SelectMany(cv => ClipToRange(s, cv, InsideTol, s.Length - InsideTol))
                : curves;
            foreach (Curve cv in toCheck)
            {
                // fibras extremas segun la direccion de este trozo de barra (en un recorrido cada tramo tiene su eje)
                XYZ t = (cv.GetEndPoint(1) - cv.GetEndPoint(0));
                t = t.GetLength() > 1e-9 ? t.Normalize() : s.DirW;
                XYZ du = Math.Abs(t.Z) > 0.99 ? XYZ.BasisX : XYZ.BasisZ.CrossProduct(t).Normalize();
                XYZ dv = t.CrossProduct(du).Normalize();
                var shifts = new List<XYZ> { XYZ.Zero, du * r, du * -r, dv * r, dv * -r };
                if (!longitudinal) { shifts.Add(t * r); shifts.Add(t * -r); }
                foreach (XYZ sh in shifts)
                {
                    Curve probe = sh.IsZeroLength() ? cv : cv.CreateTransformed(Transform.CreateTranslation(sh));
                    if (!CurveInside(solids, probe, out double outside))
                    {
                        why = "queda fuera del hormigon (" + ToMm(outside) + " mm de barra fuera; segmento de " +
                              s.LocalMm(cv.GetEndPoint(0)) + " a " + s.LocalMm(cv.GetEndPoint(1)) + ")";
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>Trozos de la curva (como lineas) con w entre w0 y w1; lo que queda fuera de ese rango no se comprueba.</summary>
        private static List<Curve> ClipToRange(BeamSection s, Curve cv, double w0, double w1)
        {
            var result = new List<Curve>();
            IList<XYZ> pts = cv.Tessellate();
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                XYZ a = pts[i], b = pts[i + 1];
                double wa = s.LocalW(a), wb = s.LocalW(b);
                if (Math.Max(wa, wb) < w0 || Math.Min(wa, wb) > w1) continue;
                XYZ lo = a, hi = b;
                if (Math.Abs(wb - wa) > 1e-12)
                {
                    double ta = Math.Max(0, Math.Min(1, (w0 - wa) / (wb - wa)));
                    double tb = Math.Max(0, Math.Min(1, (w1 - wa) / (wb - wa)));
                    double t0 = Math.Min(ta, tb), t1 = Math.Max(ta, tb);
                    if (wa < w0 || wa > w1) lo = a + (b - a) * (wa < wb ? t0 : t1);
                    if (wb < w0 || wb > w1) hi = a + (b - a) * (wa < wb ? t1 : t0);
                }
                if (lo.DistanceTo(hi) > MinSeg) result.Add(Line.CreateBound(lo, hi));
            }
            return result;
        }

        /// <summary>
        /// Longitud de la curva que queda fuera de la union de los solidos (en una esquina la
        /// barra pasa de un cimiento al siguiente); no verificable cuenta como fuera.
        /// </summary>
        private static bool CurveInside(IList<Solid> solids, Curve cv, out double outsideLen)
        {
            outsideLen = cv.Length;
            try
            {
                var opt = new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside };
                var spans = new List<(double a, double b)>();
                foreach (Solid solid in solids)
                {
                    SolidCurveIntersection ix = solid.IntersectWithCurve(cv, opt);
                    if (ix == null) continue;
                    for (int i = 0; i < ix.SegmentCount; i++)
                    {
                        CurveExtents ex = ix.GetCurveSegmentExtents(i);
                        spans.Add((Math.Min(ex.StartParameter, ex.EndParameter), Math.Max(ex.StartParameter, ex.EndParameter)));
                    }
                }
                double p0 = cv.GetEndParameter(0), p1 = cv.GetEndParameter(1);
                double range = Math.Abs(p1 - p0);
                // union de intervalos de parametro
                double covered = 0, lastEnd = double.NegativeInfinity;
                foreach (var sp in spans.OrderBy(x => x.a))
                {
                    double a = Math.Max(sp.a, lastEnd), b = sp.b;
                    if (b > a) { covered += b - a; lastEnd = b; }
                }
                double inside = range > 1e-12 ? cv.Length * covered / range : 0;
                outsideLen = Math.Max(0, cv.Length - inside);
                return outsideLen <= InsideTol;
            }
            catch { return false; }
        }

        // =================================================================
        // Utilidades
        // =================================================================
        private static string SetName(string name) =>
            System.Text.RegularExpressions.Regex.Replace(name, @"\s+(u=|w=|\(|inicio|fin|centro|tramo|resto).*$", "").Trim();

        private static void AddLine(List<Curve> list, XYZ a, XYZ b)
        {
            if (a.DistanceTo(b) > MinSeg) list.Add(Line.CreateBound(a, b));
        }

        private static Rebar Create(Document doc, Element host, RebarBarType bt, RebarStyle style, ElementId hook, bool hookLeft,
                                    XYZ normal, IList<Curve> curves, out string err)
        {
            err = null;
            try
            {
                // Revit 2027: ganchos y tratamientos de extremo van agrupados en BarTerminationsData.
                using (BarTerminationsData term = new BarTerminationsData(doc))
                {
                    if (hook != null && hook != ElementId.InvalidElementId)
                    {
                        term.HookTypeIdAtStart = hook;
                        term.HookTypeIdAtEnd = hook;
                    }
                    RebarTerminationOrientation o = hookLeft ? RebarTerminationOrientation.Left : RebarTerminationOrientation.Right;
                    term.TerminationOrientationAtStart = o;
                    term.TerminationOrientationAtEnd = o;
                    return Rebar.CreateFromCurves(doc, style, bt, host, normal.Normalize(), curves, term, true, true);
                }
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return null;
            }
        }

        private static void Finish(Document doc, Rebar r, string partition)
        {
            Parameter p = r.LookupParameter("Partition");
            if (p != null && !p.IsReadOnly && !string.IsNullOrEmpty(partition)) p.Set(partition);
            try { r.SetUnobscuredInView(doc.ActiveView, true); } catch { }
        }

        public static RebarBarType FindBarType(Document doc, string name, string use)
        {
            var all = AllBarTypes(doc);
            if (all.Count == 0)
                throw new InvalidOperationException("El proyecto no tiene ningun tipo de barra (RebarBarType). Carga una familia de armadura primero.");
            string match = MatchName(all.Select(b => b.Name), name);
            if (match == null)
                throw new InvalidOperationException("el tipo de barra de " + use + " \"" + name + "\" no existe en este proyecto; elige uno de los cargados en la ventana");
            return all.First(b => b.Name == match);
        }

        /// <summary>Id del tipo de gancho, o InvalidElementId si el nombre esta vacio. Lanza si el nombre no existe.</summary>
        public static ElementId FindHookType(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return ElementId.InvalidElementId;
            var all = AllHookTypes(doc);
            string match = MatchName(all.Select(h => h.Name), name);
            if (match == null)
                throw new InvalidOperationException("el tipo de gancho \"" + name + "\" no existe en este proyecto; elige uno de los cargados en la ventana o deja el gancho vacio");
            return all.First(h => h.Name == match).Id;
        }

        /// <summary>
        /// Nombre que corresponde a "name": coincidencia exacta, si no parcial (sin distinguir
        /// mayusculas); null si no hay ninguna. Nunca se sustituye por otro: sin coincidencia no se arma.
        /// </summary>
        public static string MatchName(IEnumerable<string> names, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var list = names.ToList();
            string exact = list.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            return list.FirstOrDefault(n => n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static List<RebarBarType> AllBarTypes(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>()
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();

        public static List<RebarHookType> AllHookTypes(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(RebarHookType)).Cast<RebarHookType>()
                .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
