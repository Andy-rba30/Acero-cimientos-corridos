using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FootingRebar
{
    /// <summary>Seccion (u, v) leida en una estacion w del eje de la viga. Coordenadas en pies.</summary>
    public sealed class ProfileStation
    {
        public double W;
        /// <summary>Contorno rectilineo, antihorario y normalizado (empieza en el vertice de menor u y, a igual u, menor v).</summary>
        public List<Pt> Polygon;
        public List<Rect> Rects;
        /// <summary>Rectangulo del alma: el rectangulo maximo de mayor canto (a igual canto, el de mayor area). Lleva el estribo.</summary>
        public Rect Web;

        public ProfileStation(double w, List<Pt> polygon, double tol)
        {
            W = w;
            Polygon = BeamProfile.Normalize(polygon);
            Rects = Rectilinear.MaximalRectangles(Polygon, tol);
            Web = BeamProfile.WebOf(Rects, tol);
        }
    }

    /// <summary>
    /// Un tramo de la viga a lo largo del eje: de seccion constante o de seccion que cambia
    /// linealmente entre sus dos extremos (cartela, canto variable). Los bordes entre tramos
    /// son escalones (dos tramos constantes de distinta seccion) o quiebros (donde empieza
    /// o termina un tramo variable).
    /// </summary>
    public sealed class ProfileSegment
    {
        public int Index;
        public double W0, W1;
        public bool Constant;
        public List<Pt> Polygon0, Polygon1;
        public Rect Web0, Web1;
        public double Length => W1 - W0;

        private double T(double w) => Length <= 1e-12 ? 0 : Math.Max(0, Math.Min(1, (w - W0) / Length));

        public Rect WebAt(double w)
        {
            if (Constant) return Web0.Clone();
            double t = T(w);
            return new Rect(Web0.U1 + (Web1.U1 - Web0.U1) * t, Web0.V1 + (Web1.V1 - Web0.V1) * t,
                            Web0.U2 + (Web1.U2 - Web0.U2) * t, Web0.V2 + (Web1.V2 - Web0.V2) * t);
        }

        public List<Pt> PolygonAt(double w)
        {
            if (Constant || Polygon0.Count != Polygon1.Count) return Polygon0;
            double t = T(w);
            var list = new List<Pt>(Polygon0.Count);
            for (int i = 0; i < Polygon0.Count; i++)
                list.Add(new Pt(Polygon0[i].U + (Polygon1[i].U - Polygon0[i].U) * t, Polygon0[i].V + (Polygon1[i].V - Polygon0[i].V) * t));
            return list;
        }

        private static string Mm(double ft) => Math.Round(ft * 304.8).ToString(CultureInfo.InvariantCulture);

        public string Describe()
        {
            string range = "tramo " + (Index + 1) + " (" + Mm(W0) + "-" + Mm(W1) + " mm): ";
            if (Constant) return range + "alma " + Mm(Web0.W) + " x " + Mm(Web0.H) + " constante";
            return range + "alma " + Mm(Web0.W) + " de canto variable " + Mm(Web0.H) + " -> " + Mm(Web1.H);
        }
    }

    /// <summary>
    /// Perfil de la viga a lo largo de su eje deducido de las secciones muestreadas en
    /// estaciones: tramos de seccion constante (agrupando estaciones iguales) y tramos de
    /// seccion variable lineal (cartelas), con los limites entre tramos afinados por el
    /// llamador (una cara plana del solido o una biseccion con mas cortes). Geometria pura,
    /// sin Revit: se puede probar en un programa de consola. Todo en pies; el origen es
    /// la cara de inicio (w = 0), y (u, v) las coordenadas comunes de todas las secciones.
    /// </summary>
    public sealed class BeamProfile
    {
        public double Length;
        public List<ProfileStation> Stations = new List<ProfileStation>();
        public List<ProfileSegment> Segments = new List<ProfileSegment>();
        /// <summary>Extension total de todas las secciones (u, v).</summary>
        public double UMin, UMax, VMin, VMax;
        public double Width => UMax - UMin;
        public double Depth => VMax - VMin;
        /// <summary>Ancho del alma (constante en toda la viga).</summary>
        public double WebWidth;
        /// <summary>Canto del alma minimo y maximo a lo largo de la viga.</summary>
        public double MinDepth, MaxDepth;
        /// <summary>Alma de menor canto (contra la que se comprueba que caben las capas).</summary>
        public Rect MinWeb;
        /// <summary>Estacion que se dibuja en el esquema de la seccion (la mas cercana al centro del vano).</summary>
        public ProfileStation Reference;
        public string KindName;
        public bool Variable => Segments.Count > 1 || Segments.Any(s => !s.Constant);

        public ProfileSegment SegmentAt(double w)
        {
            foreach (ProfileSegment s in Segments)
                if (w <= s.W1 + 1e-9) return s;
            return Segments[Segments.Count - 1];
        }

        public Rect WebAt(double w) => SegmentAt(Math.Max(0, Math.Min(Length, w))).WebAt(Math.Max(0, Math.Min(Length, w)));

        /// <summary>Cotas (w) de los limites entre tramos.</summary>
        public List<double> Kinks => Segments.Skip(1).Select(s => s.W0).ToList();

        private static string Mm(double ft) => Math.Round(ft * 304.8).ToString(CultureInfo.InvariantCulture);

        public string Describe()
        {
            string d = "alma " + Mm(WebWidth) + " x " + Mm(MinDepth) + (MaxDepth - MinDepth > 1e-9 ? "-" + Mm(MaxDepth) : "") + " mm " +
                       KindName + ", longitud " + (Length * 0.3048).ToString("0.00", CultureInfo.InvariantCulture) + " m";
            if (Variable) d += " (" + string.Join("; ", Segments.Select(s => s.Describe())) + ")";
            return d;
        }

        // ------------------------------------------------------------------
        // Utilidades de poligonos
        // ------------------------------------------------------------------

        /// <summary>Rota el contorno para que empiece en el vertice de menor u (y a igual u, menor v). No cambia el sentido.</summary>
        public static List<Pt> Normalize(List<Pt> poly)
        {
            if (poly == null || poly.Count == 0) return new List<Pt>();
            int best = 0;
            const double tol = 1e-6;
            for (int i = 1; i < poly.Count; i++)
            {
                Pt p = poly[i], b = poly[best];
                if (p.U < b.U - tol || (Math.Abs(p.U - b.U) <= tol && p.V < b.V - tol)) best = i;
            }
            var list = new List<Pt>(poly.Count);
            for (int i = 0; i < poly.Count; i++) list.Add(poly[(best + i) % poly.Count]);
            return list;
        }

        /// <summary>Mismos vertices (sin importar el orden ni el punto de arranque) con tolerancia.</summary>
        public static bool SamePolygon(IList<Pt> a, IList<Pt> b, double tol)
        {
            if (a.Count != b.Count) return false;
            foreach (Pt p in a)
                if (!b.Any(q => q.DistanceTo(p) <= 2 * tol)) return false;
            return true;
        }

        /// <summary>El rectangulo de mayor canto (a igual canto, el de mayor area): el alma, que lleva el estribo.</summary>
        public static Rect WebOf(IList<Rect> rects, double tol)
        {
            if (rects == null || rects.Count == 0) return null;
            double maxH = rects.Max(r => r.H);
            return rects.Where(r => r.H >= maxH - tol).OrderByDescending(r => r.Area).ThenBy(r => r.U1).First();
        }

        /// <summary>Nombre de la forma: rectangular, en T, en T invertida, en L, en L invertida, en I...</summary>
        public static string Kind(IList<Pt> poly, IList<Rect> rects, Rect web)
        {
            string k = Rectilinear.Kind(poly, rects);
            if ((k == "en T" || k == "en L") && rects.Count == 2 && web != null)
            {
                Rect flange = ReferenceEquals(rects[0], web) ? rects[1] : rects[0];
                if (flange.CV < web.CV - 1e-9) k += " invertida";
            }
            else if (k == "en cruz" && rects.Count == 2) k = "en I";
            return k;
        }

        private static bool Linear(IList<ProfileStation> st, int a, int b, double tol, out string why)
        {
            why = null;
            List<Pt> pa = st[a].Polygon, pb = st[b].Polygon;
            if (pa.Count != pb.Count) { why = "cambia el numero de vertices"; return false; }
            double span = st[b].W - st[a].W;
            for (int i = a; i <= b; i++)
            {
                List<Pt> p = st[i].Polygon;
                if (p.Count != pa.Count) { why = "cambia el numero de vertices en w=" + Mm(st[i].W) + " mm"; return false; }
                double t = span <= 1e-12 ? 0 : (st[i].W - st[a].W) / span;
                for (int k = 0; k < p.Count; k++)
                {
                    var e = new Pt(pa[k].U + (pb[k].U - pa[k].U) * t, pa[k].V + (pb[k].V - pa[k].V) * t);
                    if (p[k].DistanceTo(e) > 2 * tol) { why = "la seccion en w=" + Mm(st[i].W) + " mm no esta en la recta entre sus vecinas"; return false; }
                }
                if (Math.Abs(st[i].Web.U1 - st[a].Web.U1) > tol || Math.Abs(st[i].Web.U2 - st[a].Web.U2) > tol)
                { why = "el ancho del alma cambia en w=" + Mm(st[i].W) + " mm"; return false; }
            }
            return true;
        }

        /// <summary>
        /// Cota w en la que la seccion que cambia linealmente entre las estaciones a y b
        /// coincide con el poligono constante dado (el quiebro de una cartela): se resuelve
        /// vertice a vertice con los que cambian y se toma la media ponderada por su
        /// pendiente. Null si los vertices no se corresponden o el resultado no cae entre
        /// lo y hi (con una tolerancia), y entonces el llamador recurre a la biseccion.
        /// </summary>
        private static double? Kink(IList<ProfileStation> st, int a, int b, List<Pt> constant, double lo, double hi, double tol)
        {
            List<Pt> pa = st[a].Polygon, pb = st[b].Polygon;
            if (constant == null || constant.Count != pa.Count) return null;
            double span = st[b].W - st[a].W;
            if (span <= 1e-12) return null;
            double sum = 0, weight = 0;
            for (int k = 0; k < pa.Count; k++)
            {
                foreach ((double ca, double cb, double cc) in new[] { (pa[k].U, pb[k].U, constant[k].U), (pa[k].V, pb[k].V, constant[k].V) })
                {
                    double slope = (cb - ca) / span;
                    if (Math.Abs(slope) * Math.Abs(hi - lo) <= tol) continue;   // esta coordenada apenas cambia en el tramo
                    double w = st[a].W + (cc - ca) / slope;
                    sum += w * Math.Abs(slope);
                    weight += Math.Abs(slope);
                }
            }
            if (weight <= 0) return null;
            double kink = sum / weight;
            double margin = 0.25 * Math.Abs(hi - lo) + tol;
            if (kink < Math.Min(lo, hi) - margin || kink > Math.Max(lo, hi) + margin) return null;
            return Math.Max(Math.Min(lo, hi), Math.Min(Math.Max(lo, hi), kink));
        }

        private static List<Pt> Lerp(ProfileStation a, ProfileStation b, double w)
        {
            double span = b.W - a.W;
            double t = span <= 1e-12 ? 0 : (w - a.W) / span;
            var list = new List<Pt>(a.Polygon.Count);
            for (int k = 0; k < a.Polygon.Count; k++)
                list.Add(new Pt(a.Polygon[k].U + (b.Polygon[k].U - a.Polygon[k].U) * t, a.Polygon[k].V + (b.Polygon[k].V - a.Polygon[k].V) * t));
            return list;
        }

        // ------------------------------------------------------------------
        // Construccion
        // ------------------------------------------------------------------

        /// <summary>
        /// Deduce los tramos a partir de las estaciones (ordenadas por w, todas dentro de
        /// 0..length). "refine(wA, wB, poligono, enA)" devuelve la cota exacta, entre las
        /// estaciones wA y wB, donde la seccion deja de ser (enA = true) o empieza a ser
        /// (enA = false) el poligono dado: el llamador la busca en una cara del solido o
        /// por biseccion. Devuelve null y el motivo si la viga no se puede leer.
        /// </summary>
        /// <summary>
        /// Perfil de un recorrido: los perfiles de los tramos puestos uno tras otro (la cota w
        /// sigue creciendo de un tramo al siguiente). Todos tienen que tener el mismo ancho de
        /// alma; el canto puede cambiar de un tramo a otro (escalon en la union).
        /// </summary>
        public static BeamProfile Concat(IList<BeamProfile> parts, double tol, out string error)
        {
            error = null;
            if (parts == null || parts.Count == 0) { error = "recorrido vacio"; return null; }
            if (parts.Count == 1) return parts[0];
            var p = new BeamProfile();
            double w0 = 0;
            Rect first = parts[0].Segments[0].Web0;
            for (int k = 0; k < parts.Count; k++)
            {
                BeamProfile part = parts[k];
                foreach (ProfileSegment s in part.Segments)
                {
                    if (Math.Abs(s.Web0.U1 - first.U1) > tol || Math.Abs(s.Web0.W - first.W) > tol)
                    {
                        error = "el tramo " + (k + 1) + " del recorrido tiene un alma de " + Mm(s.Web0.W) + " mm y el primero de " + Mm(first.W) +
                                " mm: los cimientos encadenados tienen que tener el mismo ancho";
                        return null;
                    }
                    p.Segments.Add(new ProfileSegment
                    {
                        Index = p.Segments.Count, W0 = s.W0 + w0, W1 = s.W1 + w0, Constant = s.Constant,
                        Polygon0 = s.Polygon0, Polygon1 = s.Polygon1, Web0 = s.Web0.Clone(), Web1 = s.Web1.Clone()
                    });
                }
                foreach (ProfileStation st in part.Stations)
                    p.Stations.Add(new ProfileStation(st.W + w0, st.Polygon, tol));
                w0 += part.Length;
            }
            p.Length = w0;
            p.UMin = parts.Min(x => x.UMin); p.UMax = parts.Max(x => x.UMax);
            p.VMin = parts.Min(x => x.VMin); p.VMax = parts.Max(x => x.VMax);
            p.WebWidth = first.W;
            p.MinDepth = double.MaxValue; p.MaxDepth = 0;
            foreach (ProfileSegment s in p.Segments)
                foreach (Rect r in new[] { s.Web0, s.Web1 })
                {
                    if (r.H < p.MinDepth) { p.MinDepth = r.H; p.MinWeb = r.Clone(); }
                    p.MaxDepth = Math.Max(p.MaxDepth, r.H);
                }
            p.Reference = p.Stations.OrderBy(s => Math.Abs(s.W - 0.5 * w0)).First();
            p.KindName = parts[0].KindName + ", recorrido de " + parts.Count + " tramos";
            return p;
        }

        public static BeamProfile Build(List<ProfileStation> stations, double length, double tol,
                                        Func<double, double, List<Pt>, bool, double> refine, out string error)
        {
            error = null;
            var p = new BeamProfile { Length = length };
            if (stations == null || stations.Count < 2) { error = "hacen falta al menos dos estaciones"; return null; }
            List<ProfileStation> st = stations.OrderBy(s => s.W).ToList();
            p.Stations = st;
            int n = st.Count;
            foreach (ProfileStation s in st)
                if (s.Web == null) { error = "no se pudo descomponer la seccion en w=" + Mm(s.W) + " mm en rectangulos"; return null; }

            var same = new bool[n - 1];
            for (int i = 0; i + 1 < n; i++) same[i] = SamePolygon(st[i].Polygon, st[i + 1].Polygon, tol);

            // tramos constantes: estaciones consecutivas iguales (al menos dos)
            var runs = new List<(int a, int b)>();
            for (int i = 0; i < n;)
            {
                int j = i;
                while (j + 1 < n && same[j]) j++;
                if (j > i) runs.Add((i, j));
                i = j + 1;
            }

            var segs = new List<ProfileSegment>();
            ProfileSegment open = null;   // ultimo tramo constante, pendiente de cerrar por su W1
            string err = null;

            bool Stretch(int i0, int i1, ProfileStation prevRep, ProfileStation nextRep)
            {
                // estaciones interiores i0+1 .. i1-1 (i0 = -1 al principio, i1 = n al final)
                int first = i0 + 1, last = i1 - 1;
                int count = last - first + 1;
                if (count <= 0)
                {
                    if (i0 < 0 || i1 >= n) return true;
                    // escalon entre dos tramos constantes
                    double ws = refine(st[i0].W, st[i1].W, st[i0].Polygon, true);
                    open.W1 = ws;
                    open = null;
                    return true;
                }
                if (count == 1)
                {
                    err = "la seccion cambia entre w=" + Mm(st[Math.Max(0, i0)].W) + " y w=" + Mm(st[Math.Min(n - 1, i1)].W) +
                            " mm en un tramo demasiado corto para leerlo (una sola estacion): baja prismCheckStepMm";
                    return false;
                }
                if (!Linear(st, first, last, tol, out string why))
                {
                    err = "la seccion entre w=" + Mm(st[first].W) + " y w=" + Mm(st[last].W) + " mm no cambia de forma lineal (" + why +
                            "): solo se admiten tramos de seccion constante y cartelas rectas";
                    return false;
                }
                // quiebros con los tramos constantes vecinos: donde la recta de la cartela alcanza la
                // seccion constante (calculo exacto); si no se puede, la biseccion del llamador
                double w0 = i0 < 0 ? 0
                    : Kink(st, first, last, prevRep.Polygon, st[i0].W, st[first].W, tol) ?? refine(st[i0].W, st[first].W, st[i0].Polygon, true);
                double w1 = i1 >= n ? length
                    : Kink(st, first, last, nextRep.Polygon, st[last].W, st[i1].W, tol) ?? refine(st[last].W, st[i1].W, st[i1].Polygon, false);
                if (open != null) { open.W1 = w0; open = null; }
                var seg = new ProfileSegment { W0 = w0, W1 = w1, Constant = false };
                // en el quiebro la seccion es exactamente la del tramo constante vecino (continuidad)
                bool snap0 = prevRep != null && prevRep.Polygon.Count == st[first].Polygon.Count;
                bool snap1 = nextRep != null && nextRep.Polygon.Count == st[first].Polygon.Count;
                seg.Polygon0 = snap0 ? prevRep.Polygon : Lerp(st[first], st[last], w0);
                seg.Polygon1 = snap1 ? nextRep.Polygon : Lerp(st[first], st[last], w1);
                seg.Web0 = snap0 ? prevRep.Web.Clone() : WebOf(Rectilinear.MaximalRectangles(seg.Polygon0, tol), tol);
                seg.Web1 = snap1 ? nextRep.Web.Clone() : WebOf(Rectilinear.MaximalRectangles(seg.Polygon1, tol), tol);
                if (seg.Web0 == null || seg.Web1 == null || seg.Web0.H <= tol || seg.Web1.H <= tol)
                {
                    err = "el tramo de seccion variable entre w=" + Mm(w0) + " y w=" + Mm(w1) + " mm se queda sin canto en un extremo";
                    return false;
                }
                segs.Add(seg);
                return true;
            }

            ProfileStation Rep(int k) => st[(runs[k].a + runs[k].b) / 2];
            if (runs.Count == 0)
            {
                if (!Stretch(-1, n, null, null)) { error = err; return null; }
            }
            else
            {
                for (int k = 0; k < runs.Count; k++)
                {
                    if (!Stretch(k == 0 ? -1 : runs[k - 1].b, runs[k].a, k == 0 ? null : Rep(k - 1), Rep(k))) { error = err; return null; }
                    double w0 = segs.Count == 0 ? 0 : segs[segs.Count - 1].W1;
                    ProfileStation r = Rep(k);
                    open = new ProfileSegment
                    {
                        W0 = w0, W1 = length, Constant = true,
                        Polygon0 = r.Polygon, Polygon1 = r.Polygon, Web0 = r.Web.Clone(), Web1 = r.Web.Clone()
                    };
                    segs.Add(open);
                }
                if (!Stretch(runs[runs.Count - 1].b, n, Rep(runs.Count - 1), null)) { error = err; return null; }
            }

            for (int i = 0; i < segs.Count; i++) segs[i].Index = i;
            p.Segments = segs;
            foreach (ProfileSegment s in segs)
                if (s.Length <= 2 * tol)
                {
                    error = "el tramo " + (s.Index + 1) + " (w=" + Mm(s.W0) + " mm) tiene longitud nula";
                    return null;
                }

            // ancho del alma constante en toda la viga
            Rect first0 = segs[0].Web0;
            foreach (ProfileSegment s in segs)
                foreach (Rect r in new[] { s.Web0, s.Web1 })
                    if (Math.Abs(r.U1 - first0.U1) > tol || Math.Abs(r.U2 - first0.U2) > tol)
                    {
                        error = "el ancho del alma cambia a lo largo de la viga (" + Mm(first0.W) + " mm en el inicio, " + Mm(r.W) +
                                " mm en el tramo " + (s.Index + 1) + "): solo se admiten vigas de ancho constante";
                        return null;
                    }
            p.WebWidth = first0.W;

            // extension y cantos
            p.UMin = double.MaxValue; p.UMax = double.MinValue; p.VMin = double.MaxValue; p.VMax = double.MinValue;
            foreach (List<Pt> poly in st.Select(s => s.Polygon).Concat(segs.SelectMany(s => new[] { s.Polygon0, s.Polygon1 })))
                foreach (Pt q in poly)
                {
                    p.UMin = Math.Min(p.UMin, q.U); p.UMax = Math.Max(p.UMax, q.U);
                    p.VMin = Math.Min(p.VMin, q.V); p.VMax = Math.Max(p.VMax, q.V);
                }
            p.MinDepth = double.MaxValue; p.MaxDepth = 0;
            foreach (ProfileSegment s in segs)
                foreach (Rect r in new[] { s.Web0, s.Web1 })
                {
                    if (r.H < p.MinDepth) { p.MinDepth = r.H; p.MinWeb = r.Clone(); }
                    p.MaxDepth = Math.Max(p.MaxDepth, r.H);
                }

            p.Reference = st.OrderBy(s => Math.Abs(s.W - 0.5 * length)).First();
            p.KindName = Kind(p.Reference.Polygon, p.Reference.Rects, p.Reference.Web);
            if (p.Variable)
                p.KindName += " de seccion variable (" + segs.Count + (segs.Count == 1 ? " tramo" : " tramos") + ")";
            return p;
        }
    }
}
