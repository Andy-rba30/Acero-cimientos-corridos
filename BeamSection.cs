using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace StripFootingRebar
{
    /// <summary>
    /// Geometria de una viga deducida del solido real del elemento: se toma el eje (la
    /// curva de ubicacion), se corta el solido con rebanadas finas perpendiculares al eje
    /// en estaciones a lo largo de toda la longitud y se lee el contorno de cada una. Cada
    /// contorno tiene que ser un poligono rectilineo (bordes paralelos a dos ejes: seccion
    /// rectangular, en T, en L...). Las estaciones se agrupan en tramos de seccion constante
    /// o de canto variable lineal (BeamProfile).
    ///
    /// Sistema local: w a lo largo del eje desde la cara de inicio, u horizontal
    /// perpendicular al eje, v perpendicular a los dos (vertical en una viga horizontal).
    /// Origen en la esquina minima (u, v) de las secciones a la cota de la cara de inicio.
    /// Todo en pies (unidades internas de Revit).
    /// </summary>
    public sealed class BeamSection
    {
        public const double MmPerFt = 304.8;
        public static double Mm(double mm) => mm / MmPerFt;
        public static double ToMm(double ft) => Math.Round(ft * MmPerFt);

        public Element Host;
        /// <summary>Solido contra el que se comprueban las barras.</summary>
        public Solid HostSolid;
        /// <summary>Solido tal y como lo ve Revit (cortado por uniones), o el mismo que HostSolid.</summary>
        public Solid CutSolid;
        /// <summary>Solido del que se han leido las secciones (geometria completa de la familia si se recupero).</summary>
        public Solid SectionSolid;
        public bool UsedOriginal;
        /// <summary>
        /// Tramo recto recortado de un suelo (Floor) con esquinas: el resto del suelo es
        /// hormigon contiguo (otro tramo del mismo suelo al que este llega en T o en esquina).
        /// </summary>
        public bool IsFloorStrip;
        /// <summary>Si esta seccion representa un recorrido de varios cimientos encadenados: el mapeo (u, v, w) pasa por la cadena.</summary>
        public FootingChain Chain;
        public string JoinedNote;

        public XYZ Origin, DirU, DirV, DirW;
        public BeamProfile Profile;
        public double Length => Profile.Length;
        public string KindName => Profile.KindName;

        public static string LastError;

        public XYZ World(double u, double v, double w) =>
            Chain != null ? Chain.World(u, v, w) : Origin + DirU * u + DirV * v + DirW * w;

        /// <summary>
        /// Hormigon contiguo en los extremos (los cimientos a los que llega en T o en esquina):
        /// las barras que se anclan en ellos tambien se comprueban contra estos solidos.
        /// </summary>
        public List<Solid> ExtraSolids = new List<Solid>();
        /// <summary>True cuando ExtraSolids ya se ha buscado en el modelo (RebarGenerator.LoadAdjacent).</summary>
        public bool AdjacentLoaded;

        /// <summary>Solidos contra los que se comprueban las barras (uno por tramo en una cadena, mas el hormigon contiguo).</summary>
        public List<Solid> AllSolids() =>
            (Chain != null ? Chain.Solids() : new List<Solid> { HostSolid }).Concat(ExtraSolids ?? new List<Solid>()).ToList();

        /// <summary>Seccion sintetica de un recorrido: el perfil de los tramos concatenado y el mapeo por la cadena.</summary>
        public static BeamSection ForChain(FootingChain chain, double tol, out string error)
        {
            BeamSection first = chain.Segs[0].Section;
            BeamProfile prof = BeamProfile.Concat(chain.Segs.Select(s => s.Section.Profile).ToList(), chain.Segs.Select(s => s.DV).ToList(), tol, out error);
            if (prof == null) return null;
            return new BeamSection
            {
                Host = first.Host, HostSolid = first.HostSolid, CutSolid = first.CutSolid, SectionSolid = first.SectionSolid,
                UsedOriginal = chain.Segs.Any(s => s.Section.UsedOriginal),
                JoinedNote = chain.Segs.Count > 1 ? " (recorrido de " + chain.Segs.Count + " tramos encadenados)" : first.JoinedNote,
                Origin = first.Origin, DirU = first.DirU, DirV = first.DirV, DirW = first.DirW, Profile = prof, Chain = chain
            };
        }

        public Pt Local(XYZ p)
        {
            XYZ d = p - Origin;
            return new Pt(d.DotProduct(DirU), d.DotProduct(DirV));
        }

        public double LocalW(XYZ p) => Chain != null ? Chain.WOf(p) : (p - Origin).DotProduct(DirW);

        public string LocalMm(XYZ p)
        {
            Pt l = Local(p);
            return "u=" + ToMm(l.U) + " v=" + ToMm(l.V) + " w=" + ToMm(LocalW(p));
        }

        public string Describe() => Profile.Describe() + (JoinedNote ?? "");

        // ------------------------------------------------------------------
        // Deduccion
        // ------------------------------------------------------------------
        /// <param name="forceDir">Direccion del eje impuesta (null = se deduce del elemento).</param>
        /// <param name="clip">Prisma que recorta el trozo del elemento a leer (un tramo recto de un suelo); null = el elemento entero.</param>
        /// <param name="piece">
        /// Trozo macizo del elemento que se lee tal cual: uno de los trozos en que lo parten los
        /// elementos unidos que lo cortan (ver SplitAlongAxis); null = el solido del elemento.
        /// </param>
        public static BeamSection Probe(Document doc, Element host, AppConfig cfg, XYZ forceDir = null, Solid clip = null, Solid piece = null)
        {
            LastError = null;
            var s = new BeamSection { Host = host };

            List<Solid> cut;
            if (piece != null)
            {
                if (piece.Volume < 1e-9) { LastError = "el trozo del elemento no tiene hormigon"; return null; }
                cut = new List<Solid> { piece };
            }
            else
            {
                cut = Solids(host);
                if (cut.Count == 0) { LastError = "el elemento no tiene geometria solida"; return null; }
                if (cut.Count > 1 && cut[1].Volume > 0.01 * cut[0].Volume)
                {
                    LastError = "el elemento tiene " + cut.Count + " solidos; se esperaba uno solo (viga maciza)";
                    return null;
                }
                if (clip != null)
                {
                    Solid clipped;
                    try { clipped = BooleanOperationsUtils.ExecuteBooleanOperation(cut[0], clip, BooleanOperationsType.Intersect); }
                    catch (Exception ex) { LastError = "no se pudo recortar el tramo del suelo (" + ex.Message + ")"; return null; }
                    if (clipped == null || clipped.Volume < 1e-9) { LastError = "el tramo del suelo no tiene hormigon"; return null; }
                    cut = new List<Solid> { clipped };
                    s.IsFloorStrip = true;
                }
            }
            s.CutSolid = cut[0];
            s.SectionSolid = cut[0];
            s.HostSolid = cut[0];

            // Geometria completa de la familia, por si otros elementos (columnas, losas) le
            // han quitado hormigon con uniones o recortes. Un trozo (piece) ya viene cortado a
            // proposito por esos elementos: se lee tal cual, sin recuperar nada.
            int mode = cfg.JoinedIndex;   // 0 auto, 1 cortada, 2 completa
            Solid whole = null;
            if (piece == null && mode != 1 && host is FamilyInstance fi)
            {
                whole = OriginalSolid(fi, s.CutSolid);
                if (whole != null && whole.Volume <= s.CutSolid.Volume * 1.001) whole = null;
            }
            if (whole != null) { s.SectionSolid = whole; s.UsedOriginal = true; }
            Solid lengthSolid = mode == 2 && whole != null ? whole : s.CutSolid;

            // --- eje ---
            string axisErr = null;
            XYZ dirW = forceDir != null ? forceDir.Normalize() : Axis(host, s.CutSolid, out axisErr);
            if (dirW == null) { LastError = axisErr ?? "eje no valido"; return null; }
            s.DirW = dirW;
            s.DirU = XYZ.BasisZ.CrossProduct(dirW).Normalize();
            s.DirV = dirW.CrossProduct(s.DirU).Normalize();
            if (s.DirV.Z < 0) { s.DirU = -s.DirU; s.DirV = -s.DirV; }

            // --- extension en el sistema local (marco provisional) ---
            s.Origin = XYZ.Zero;
            LocalBounds(s, lengthSolid, out double lu0, out double lu1, out double lv0, out double lv1, out double w0, out double w1);
            LocalBounds(s, s.SectionSolid, out double su0, out double su1, out double sv0, out double sv1, out _, out _);
            double uB = Math.Min(lu0, su0), uT = Math.Max(lu1, su1), vB = Math.Min(lv0, sv0), vT = Math.Max(lv1, sv1);
            s.Origin = s.DirU * uB + s.DirV * vB + s.DirW * w0;
            double length = w1 - w0;
            double slice = Mm(cfg.ProbeSliceMm);
            double half = 0.5 * slice;
            double tol = Mm(cfg.PrismCheckToleranceMm);
            double angleTol = cfg.RectilinearAngleDeg * Math.PI / 180;
            double uSpan = uT - uB + 2, vSpan = vT - vB + 2;
            if (length < 10 * slice) { LastError = "la viga es demasiado corta (" + ToMm(length) + " mm)"; return null; }

            // --- estaciones ---
            List<Pt> SlicePolygon(double w, out string why)
            {
                List<XYZ> world = Slice(s, s.SectionSolid, w, half, uSpan, vSpan, out why);
                if (world == null) return null;
                var local = world.Select(p => s.Local(p)).ToList();
                local = Rectilinear.Simplify(local, tol);
                if (local.Count < 4) { why = "la seccion tiene menos de 4 vertices"; return null; }
                if (!Rectilinear.IsRectilinear(local, angleTol, out string badEdge))
                {
                    why = "la seccion no es rectilinea (" + badEdge + "): solo se admiten secciones con bordes paralelos a dos ejes " +
                          "(rectangular, en T, en L...)";
                    return null;
                }
                local = Rectilinear.Simplify(Rectilinear.Snap(local, tol), tol);
                if (Rectilinear.SignedArea(local) < 0) local.Reverse();
                return local;
            }

            int n = Math.Max(5, (int)Math.Ceiling(length / Mm(cfg.PrismCheckStepMm)));
            n = Math.Min(n, 400);
            double margin = Math.Max(slice, Math.Min(0.02 * length, Mm(60)));
            var raw = new List<(double w, List<Pt> poly)>();
            for (int k = 0; k <= n; k++)
            {
                double w = margin + (length - 2 * margin) * k / n;
                List<Pt> poly = SlicePolygon(w, out string why);
                if (poly == null)
                {
                    LastError = "no se pudo leer la seccion a " + ToMm(w) + " mm de la cara de inicio: " + why;
                    return null;
                }
                raw.Add((w, poly));
            }

            // origen definitivo en la esquina minima de las secciones
            double uMin = raw.Min(r => r.poly.Min(p => p.U)), vMin = raw.Min(r => r.poly.Min(p => p.V));
            s.Origin = s.Origin + s.DirU * uMin + s.DirV * vMin;
            var stations = raw.Select(r => new ProfileStation(r.w, r.poly.Select(p => new Pt(p.U - uMin, p.V - vMin)).ToList(), tol)).ToList();

            // --- limites entre tramos: cara plana perpendicular al eje, o biseccion ---
            double Refine(double wA, double wB, List<Pt> reference, bool atA)
            {
                double? face = FaceBetween(s, s.SectionSolid, wA, wB);
                if (face.HasValue) return face.Value;
                double lo = wA, hi = wB;
                for (int i = 0; i < 7; i++)
                {
                    double mid = 0.5 * (lo + hi);
                    List<Pt> pm = SlicePolygon(mid, out _);
                    bool same = pm != null && BeamProfile.SamePolygon(pm.Select(p => new Pt(p.U - uMin, p.V - vMin)).ToList(), reference, tol);
                    if (same == atA) lo = mid; else hi = mid;
                }
                return 0.5 * (lo + hi);
            }

            s.Profile = BeamProfile.Build(stations, length, tol, Refine, out string err);
            if (s.Profile == null) { LastError = err; return null; }

            // --- solido de comprobacion ---
            if (s.UsedOriginal)
            {
                s.HostSolid = whole;
                if (mode != 2)
                {
                    try
                    {
                        Solid box = Box(s, -uSpan, 2 * uSpan, -vSpan, 2 * vSpan, 0, length);
                        Solid clipped = BooleanOperationsUtils.ExecuteBooleanOperation(whole, box, BooleanOperationsType.Intersect);
                        if (clipped != null && clipped.Volume > 1e-9) s.HostSolid = clipped;
                    }
                    catch { }
                }
                s.JoinedNote = " (unida a otros elementos: seccion de la geometria completa de la familia" +
                               (mode == 2 ? " y tambien su longitud" : ", longitud del solido cortado") + ", " +
                               Vol(whole) + " m3 frente a " + Vol(s.CutSolid) + " m3 visibles)";
            }
            return s;
        }

        private static string Vol(Solid s) => (s.Volume * 0.0283168).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------
        // Elemento partido en trozos por los elementos unidos que lo cortan
        // ------------------------------------------------------------------

        /// <summary>Longitud minima (mm) de un trozo para armarlo; los retales mas cortos se ignoran.</summary>
        public const double MinPieceMm = 100;

        /// <summary>
        /// Trozos macizos separados en que queda el solido visible del elemento, ordenados a lo
        /// largo de su eje. Un muro (sobrecimiento) o un cimiento unido a columnas con
        /// prioridad para la columna (Unir geometria + Cambiar orden de union) se queda sin
        /// hormigon donde se cruza con cada columna: Revit lo devuelve como un solo solido con
        /// varios trozos sueltos. Cada trozo se arma como un tramo aparte, asi no se pone
        /// armadura del muro dentro de la columna. Null si el solido es una sola pieza, o si se
        /// va a leer la geometria completa de la familia (modo auto / completa con una familia,
        /// que no esta partida y se arma de un tiron como hasta ahora). Con "error" si los
        /// trozos se solapan a lo largo del eje (no es un tramo recto partido).
        /// </summary>
        public static List<Solid> SplitAlongAxis(Element host, AppConfig cfg, out string error, out string note)
        {
            error = null;
            note = null;
            List<Solid> raw = Solids(host);
            if (raw.Count == 0) return null;   // Probe dara el motivo

            var pieces = new List<Solid>();
            foreach (Solid sol in raw)
            {
                IList<Solid> parts = null;
                try { parts = SolidUtils.SplitVolumes(sol); } catch { }
                if (parts == null || parts.Count == 0) pieces.Add(sol);
                else foreach (Solid p in parts) if (p != null && p.Volume > 1e-9) pieces.Add(p);
            }
            if (pieces.Count <= 1) return null;

            // con una familia en modo auto / completa la seccion se lee de su geometria completa
            // (la de antes de las uniones), que no esta partida: se arma entera, como hasta ahora
            if (cfg.JoinedIndex != 1 && host is FamilyInstance fi)
            {
                Solid whole = OriginalSolid(fi, raw[0]);
                if (whole != null && whole.Volume > raw[0].Volume * 1.001) return null;
            }

            XYZ axis = Axis(host, pieces.OrderByDescending(p => p.Volume).First(), out _);
            if (axis == null) return null;   // Probe dara el motivo

            // extension de cada trozo a lo largo del eje
            var ranges = new List<(Solid solid, double w0, double w1)>();
            foreach (Solid p in pieces)
            {
                double w0 = double.MaxValue, w1 = double.MinValue;
                foreach (Edge ed in p.Edges)
                    foreach (XYZ q in ed.Tessellate())
                    {
                        double w = q.DotProduct(axis);
                        w0 = Math.Min(w0, w);
                        w1 = Math.Max(w1, w);
                    }
                ranges.Add((p, w0, w1));
            }
            ranges = ranges.OrderBy(r => r.w0).ToList();

            double tol = Mm(cfg.PrismCheckToleranceMm);
            var kept = new List<Solid>();
            var skipped = new List<string>();
            double last = double.NegativeInfinity;
            foreach ((Solid solid, double w0, double w1) in ranges)
            {
                if (w1 - w0 < Mm(MinPieceMm)) { skipped.Add(ToMm(w1 - w0) + " mm"); continue; }
                if (w0 < last - tol)
                {
                    error = "el elemento tiene " + pieces.Count + " solidos que se solapan a lo largo del eje; se esperaba un solo solido macizo " +
                            "o trozos consecutivos separados por los elementos unidos que lo cortan";
                    return null;
                }
                last = w1;
                kept.Add(solid);
            }
            if (kept.Count == 0)
            {
                error = "el solido esta partido en " + pieces.Count + " trozos y todos miden menos de " + MinPieceMm + " mm a lo largo del eje";
                return null;
            }
            note = "el solido visible esta partido en " + pieces.Count + " trozos a lo largo del eje (columnas u otros elementos unidos con " +
                   "prioridad le quitan el hormigon donde se cruzan): cada trozo se arma como un tramo aparte y donde no hay hormigon del " +
                   "elemento no se pone armadura; usa la prolongacion en inicio / fin para anclar las barras dentro de las columnas" +
                   (skipped.Count > 0 ? ". Se ignoran " + skipped.Count + " retal(es) de menos de " + MinPieceMm + " mm (" + string.Join(", ", skipped) + ")" : "");
            return kept;
        }

        /// <summary>Direccion del eje de la viga: la curva de ubicacion, la orientacion de la familia o el lado largo de la caja.</summary>
        private static XYZ Axis(Element host, Solid solid, out string error)
        {
            error = null;
            XYZ d = null;
            if (host.Location is LocationCurve lc && lc.Curve != null)
            {
                if (lc.Curve is Line ln) d = ln.Direction;
                else { error = "el eje del elemento no es recto (curva de ubicacion en arco o spline): solo se arman tramos rectos"; return null; }
            }
            if (d == null && host is Autodesk.Revit.DB.WallFoundation wf)
            {
                // cimentacion de muro: el eje es el del muro que la lleva
                try
                {
                    if (host.Document.GetElement(wf.WallId) is Wall wall && wall.Location is LocationCurve wlc)
                    {
                        if (wlc.Curve is Line wl) d = wl.Direction;
                        else { error = "el muro de la cimentacion no es recto (arco): solo se arman tramos rectos"; return null; }
                    }
                }
                catch { }
            }
            if (d == null && host is FamilyInstance fi)
            {
                try
                {
                    XYZ h = fi.HandOrientation;
                    if (h != null && h.GetLength() > 0.5) d = h;
                }
                catch { }
            }
            if (d == null && host is Floor)
            {
                // suelo: no tiene curva de ubicacion; el eje es la direccion dominante de su contorno
                try { d = FloorStrips.DominantDirection(solid); } catch { }
            }
            if (d == null)
            {
                ColumnBounds(solid, out XYZ min, out XYZ max);
                d = max.X - min.X >= max.Y - min.Y ? XYZ.BasisX : XYZ.BasisY;
            }
            if (d.GetLength() < 1e-9) { error = "no se pudo determinar el eje de la viga"; return null; }
            d = d.Normalize();
            if (Math.Abs(d.Z) > 0.999) { error = "el eje de la viga es vertical: usa el add-in de columnas"; return null; }
            return d;
        }

        private static void ColumnBounds(Solid s, out XYZ min, out XYZ max)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            foreach (Edge ed in s.Edges)
                foreach (XYZ p in ed.Tessellate())
                {
                    x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                    x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
                }
            min = new XYZ(x0, y0, z0);
            max = new XYZ(x1, y1, z1);
        }

        /// <summary>Extension del solido en el sistema local actual de la seccion.</summary>
        private static void LocalBounds(BeamSection s, Solid solid, out double u0, out double u1, out double v0, out double v1, out double w0, out double w1)
        {
            u0 = double.MaxValue; v0 = double.MaxValue; w0 = double.MaxValue;
            u1 = double.MinValue; v1 = double.MinValue; w1 = double.MinValue;
            foreach (Edge ed in solid.Edges)
                foreach (XYZ p in ed.Tessellate())
                {
                    XYZ d = p - s.Origin;
                    double u = d.DotProduct(s.DirU), v = d.DotProduct(s.DirV), w = d.DotProduct(s.DirW);
                    u0 = Math.Min(u0, u); u1 = Math.Max(u1, u);
                    v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
                    w0 = Math.Min(w0, w); w1 = Math.Max(w1, w);
                }
        }

        // ------------------------------------------------------------------
        // Rebanadas
        // ------------------------------------------------------------------

        /// <summary>
        /// Contorno (en coordenadas del modelo) de la seccion perpendicular al eje a la cota
        /// w: se interseca con una caja fina y se lee la tapa de la rebanada del lado +w. Null
        /// con el motivo si la seccion esta partida, es hueca o tiene bordes curvos.
        /// </summary>
        private static List<XYZ> Slice(BeamSection s, Solid solid, double w, double half, double uSpan, double vSpan, out string why)
        {
            why = null;
            Solid box = Box(s, -1, uSpan, -1, vSpan, w - half, 2 * half);
            Solid piece;
            try { piece = BooleanOperationsUtils.ExecuteBooleanOperation(solid, box, BooleanOperationsType.Intersect); }
            catch (Exception ex) { why = "fallo la operacion booleana (" + ex.Message + ")"; return null; }
            if (piece == null || piece.Volume < 1e-9) { why = "no hay hormigon a esa cota"; return null; }

            var caps = new List<PlanarFace>();
            foreach (Face f in piece.Faces)
                if (f is PlanarFace pf && pf.FaceNormal.DotProduct(s.DirW) > 0.99 && Math.Abs(s.LocalW(pf.Origin) - (w + half)) < 0.2 * half)
                    caps.Add(pf);
            if (caps.Count == 0) { why = "la rebanada no tiene tapa plana"; return null; }
            if (caps.Count > 1) { why = "la seccion esta partida en " + caps.Count + " trozos"; return null; }

            IList<CurveLoop> loops = caps[0].GetEdgesAsCurveLoops();
            if (loops.Count != 1) { why = "la seccion es hueca (" + loops.Count + " contornos)"; return null; }

            var pts = new List<XYZ>();
            foreach (Curve c in loops[0])
            {
                if (!(c is Line))
                {
                    why = "tiene bordes curvos (esquinas redondeadas o seccion circular); solo se admiten secciones poligonales";
                    return null;
                }
                pts.Add(c.GetEndPoint(0));
            }
            return pts;
        }

        /// <summary>Cota w de la cara plana perpendicular al eje que haya entre wA y wB (la mas cercana al centro si hay varias); null si no hay.</summary>
        private static double? FaceBetween(BeamSection s, Solid solid, double wA, double wB)
        {
            double lo = Math.Min(wA, wB), hi = Math.Max(wA, wB), mid = 0.5 * (lo + hi);
            double? best = null;
            double bestD = double.MaxValue;
            foreach (Face f in solid.Faces)
            {
                if (!(f is PlanarFace pf) || Math.Abs(pf.FaceNormal.DotProduct(s.DirW)) < 0.999) continue;
                double w = s.LocalW(pf.Origin);
                if (w <= lo || w >= hi) continue;
                double d = Math.Abs(w - mid);
                if (d < bestD) { bestD = d; best = w; }
            }
            return best;
        }

        /// <summary>Caja en coordenadas locales: (u1..u2, v1..v2) desde w0, extruida h a lo largo del eje.</summary>
        private static Solid Box(BeamSection s, double u1, double u2, double v1, double v2, double w0, double h)
        {
            var loop = new CurveLoop();
            XYZ p1 = s.World(u1, v1, w0), p2 = s.World(u2, v1, w0), p3 = s.World(u2, v2, w0), p4 = s.World(u1, v2, w0);
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p4));
            loop.Append(Line.CreateBound(p4, p1));
            return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, s.DirW, h);
        }

        // ------------------------------------------------------------------
        // Solidos
        // ------------------------------------------------------------------
        private static Options GeometryOptions() =>
            new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false };

        /// <summary>Todos los solidos con volumen del elemento, de mayor a menor.</summary>
        public static List<Solid> Solids(Element e) => ScanSolids(e.get_Geometry(GeometryOptions()));

        private static List<Solid> ScanSolids(GeometryElement ge)
        {
            var list = new List<Solid>();
            if (ge == null) return list;
            void Scan(IEnumerable<GeometryObject> objs)
            {
                foreach (GeometryObject go in objs)
                {
                    if (go is Solid sol) { if (sol.Volume > 1e-9) list.Add(sol); }
                    else if (go is GeometryInstance gi) Scan(gi.GetInstanceGeometry());
                }
            }
            Scan(ge);
            return list.OrderByDescending(x => x.Volume).ToList();
        }

        /// <summary>
        /// Solido de la familia antes de uniones y cortes (GetOriginalGeometry). La API no
        /// garantiza el sistema de coordenadas en que lo devuelve: se prueba tal cual y
        /// transformado por la instancia, y se elige el que contiene al solido cortado.
        /// Null si no hay nada mejor que el solido cortado.
        /// </summary>
        private static Solid OriginalSolid(FamilyInstance fi, Solid cut)
        {
            List<Solid> raw;
            try { raw = ScanSolids(fi.GetOriginalGeometry(GeometryOptions())); }
            catch { return null; }
            if (raw.Count == 0) return null;
            var candidates = new List<Solid> { raw[0] };
            try
            {
                Transform t = fi.GetTransform();
                if (t != null && !t.IsIdentity) candidates.Add(SolidUtils.CreateTransformed(raw[0], t));
            }
            catch { }
            foreach (Solid c in candidates)
            {
                if (c.Volume < cut.Volume * 0.999) continue;
                try
                {
                    Solid common = BooleanOperationsUtils.ExecuteBooleanOperation(c, cut, BooleanOperationsType.Intersect);
                    if (common != null && common.Volume >= 0.98 * cut.Volume) return c;
                }
                catch { }
            }
            return null;
        }

        internal static string TypeNameOf(Document doc, Element e)
        {
            ElementId tid = e.GetTypeId();
            Element t = (tid != null && tid != ElementId.InvalidElementId) ? doc.GetElement(tid) : null;
            return t?.Name ?? e.Name;
        }
    }
}
