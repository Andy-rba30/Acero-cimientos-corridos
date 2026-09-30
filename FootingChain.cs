using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace FootingRebar
{
    /// <summary>Un tramo recto de una cadena de cimientos: el elemento, su seccion y donde empieza en el recorrido.</summary>
    public sealed class ChainSeg
    {
        public HostAnalysis Item;
        public BeamSection Section => Item.Section;
        /// <summary>Cota del recorrido (pies) en la que empieza este tramo.</summary>
        public double W0;
        public double Length => Section.Length;
        public double W1 => W0 + Length;
        /// <summary>Extremos del eje del tramo (centro del alma) en coordenadas del modelo.</summary>
        public XYZ Start, End;
        public XYZ Dir => Section.DirW;
    }

    /// <summary>
    /// Recorrido de un cimiento corrido: varios elementos rectos encadenados por sus
    /// extremos (esquinas). Las barras longitudinales siguen la cadena doblando en cada
    /// esquina; los estribos van tramo a tramo. El recorrido se mide con una cota w
    /// continua (0 en el inicio del primer tramo) y cada tramo conserva su propio sistema
    /// local (u, v, w) para la seccion.
    /// </summary>
    public sealed class FootingChain
    {
        public int Number;
        public List<ChainSeg> Segs = new List<ChainSeg>();
        public double Length => Segs.Count == 0 ? 0 : Segs[Segs.Count - 1].W1;
        public bool HasCorners => Segs.Count > 1;

        public string Tag => "[cadena " + Number + ": " + string.Join(" > ", Segs.Select(s => s.Item.Label)) + "] ";

        public static double MmPerFt => BeamSection.MmPerFt;

        /// <summary>Tramo que contiene la cota w del recorrido (los extremos se prolongan con el primer / ultimo tramo).</summary>
        public ChainSeg SegAt(double w)
        {
            foreach (ChainSeg s in Segs)
                if (w <= s.W1 + 1e-9) return s;
            return Segs[Segs.Count - 1];
        }

        /// <summary>Punto del modelo para (u, v) de la seccion y cota w del recorrido.</summary>
        public XYZ World(double u, double v, double w)
        {
            ChainSeg s = SegAt(w);
            return s.Section.World(u, v, w - s.W0);
        }

        /// <summary>Cota w del recorrido de un punto del modelo: la del tramo cuyo eje pasa mas cerca.</summary>
        public double WOf(XYZ p)
        {
            ChainSeg best = null;
            double bestD = double.MaxValue, bestW = 0;
            foreach (ChainSeg s in Segs)
            {
                double wl = s.Section.LocalW(p);
                double wc = Math.Max(0, Math.Min(s.Length, wl));
                XYZ axisPt = s.Start + s.Dir * wc;
                double d = new XYZ(p.X - axisPt.X, p.Y - axisPt.Y, 0).GetLength();
                if (d < bestD) { bestD = d; best = s; bestW = s.W0 + wl; }
            }
            return best == null ? 0 : bestW;
        }

        /// <summary>Todos los solidos de comprobacion de la cadena.</summary>
        public List<Solid> Solids() => Segs.Select(s => s.Section.HostSolid).ToList();

        /// <summary>
        /// Puntos del modelo de una barra a la distancia u del borde de la seccion, con la
        /// trayectoria (w, v) dada: dentro de cada tramo los puntos van en su sistema local
        /// y en cada esquina se anade el punto donde se cortan las dos lineas de barra
        /// (la barra dobla siguiendo el recorrido). Si en la esquina las lineas son
        /// paralelas (tramos alineados) se enlazan por el punto medio.
        /// </summary>
        public List<XYZ> Polyline(double u, List<(double w, double v)> path)
        {
            var pts = new List<XYZ>();
            void Add(XYZ p)
            {
                if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(p) > 0.003) pts.Add(p);
            }
            for (int i = 0; i + 1 < path.Count; i++)
            {
                (double wa, double va) = path[i];
                (double wb, double vb) = path[i + 1];
                Add(World(u, va, wa));
                foreach (ChainSeg s in Segs.Skip(1))
                {
                    double wj = s.W0;
                    if (wj <= wa + 1e-9 || wj >= wb - 1e-9) continue;
                    double t = (wj - wa) / (wb - wa);
                    double vj = va + (vb - va) * t;
                    ChainSeg prev = Segs[Segs.IndexOf(s) - 1];
                    XYZ pa = prev.Section.World(u, vj, prev.Length), da = prev.Dir;
                    XYZ pb = s.Section.World(u, vj, 0), db = s.Dir;
                    Add(Corner(pa, da, pb, db));
                }
                Add(World(u, vb, wb));
            }
            return pts;
        }

        /// <summary>Interseccion en planta de la recta (pa, da) con la (pb, db), a la cota media; punto medio si son paralelas.</summary>
        private static XYZ Corner(XYZ pa, XYZ da, XYZ pb, XYZ db)
        {
            double ax = da.X, ay = da.Y, bx = db.X, by = db.Y;
            double den = ax * by - ay * bx;
            if (Math.Abs(den) < 1e-6) return 0.5 * (pa + pb);
            double dx = pb.X - pa.X, dy = pb.Y - pa.Y;
            double t = (dx * by - dy * bx) / den;
            return new XYZ(pa.X + ax * t, pa.Y + ay * t, 0.5 * (pa.Z + pb.Z));
        }

        // ------------------------------------------------------------------
        // Deteccion de cadenas
        // ------------------------------------------------------------------

        /// <summary>Lo que se prolonga el eje de un tramo mas alla de su cara extrema para ver si entra en el hormigon del otro (mm).</summary>
        public const double ContactReachMm = 30;

        /// <summary>
        /// Encadena los elementos armables por sus extremos: dos extremos a menos de una
        /// anchura de cimiento (mas 100 mm) se unen, siempre que los dos tramos se toquen ahi
        /// (si entre las dos caras hay un hueco, una columna unida con prioridad que corta a
        /// los dos o cualquier otro elemento, no es una esquina: cada tramo termina en su
        /// cara y se arma aparte); en un nudo con mas de dos extremos se enlazan primero los
        /// dos tramos que menos giran (el cimiento que "pasa"), y el resto empieza o termina
        /// ahi (un cimiento que llega en T no se encadena). Los tramos que quedan recorridos
        /// al reves se vuelven a leer con el eje invertido.
        /// </summary>
        public static List<FootingChain> Build(Document doc, IList<HostAnalysis> items, AppConfig cfg, out List<string> notes)
        {
            notes = new List<string>();
            var segs = new List<ChainSeg>();
            foreach (HostAnalysis it in items)
                if (it.CanBuild) segs.Add(Make(it));
            int n = segs.Count;
            var chains = new List<FootingChain>();
            if (n == 0) return chains;

            // extremos: (segmento, esEnd)
            XYZ EndPt(int i, bool end) => end ? segs[i].End : segs[i].Start;
            XYZ OutDir(int i, bool end) => end ? segs[i].Dir : -segs[i].Dir;   // direccion "hacia fuera" del tramo en ese extremo
            double Tol(int i, int j) => Math.Max(segs[i].Section.Profile.Width, segs[j].Section.Profile.Width) + 100 / MmPerFt;
            // los dos tramos se tocan en esos extremos: el eje de uno, prolongado un poco mas alla de su cara, entra en el hormigon del otro
            double reach = ContactReachMm / MmPerFt;
            bool Touch(int i, bool ei, int j, bool ej) =>
                Enters(EndPt(i, ei), OutDir(i, ei), segs[j].Section, reach) || Enters(EndPt(j, ej), OutDir(j, ej), segs[i].Section, reach);

            // enlaces candidatos: pares de extremos cercanos, ordenados por lo poco que giran
            var links = new List<(int i, bool ei, int j, bool ej, double turn)>();
            var gaps = new List<string>();
            for (int i = 0; i < n; i++)
                foreach (bool ei in new[] { false, true })
                    for (int j = i + 1; j < n; j++)
                        foreach (bool ej in new[] { false, true })
                        {
                            XYZ a = EndPt(i, ei), b = EndPt(j, ej);
                            double dist = new XYZ(a.X - b.X, a.Y - b.Y, 0).GetLength();
                            if (dist > Tol(i, j)) continue;
                            if (Math.Abs(a.Z - b.Z) > Tol(i, j)) continue;
                            if (!Touch(i, ei, j, ej))
                            {
                                // hay un hueco entre las dos caras (una columna unida con prioridad, otro elemento): no es una esquina
                                gaps.Add(segs[i].Item.Label + " y " + segs[j].Item.Label + " (" + BeamSection.ToMm(dist) + " mm)");
                                continue;
                            }
                            // giro: un tramo que sale por un extremo entra en el otro por el suyo; 0 = alineados
                            double cos = OutDir(i, ei).DotProduct(-OutDir(j, ej));
                            links.Add((i, ei, j, ej, Math.Acos(Math.Max(-1, Math.Min(1, cos)))));
                        }
            links = links.OrderBy(l => l.turn).ToList();
            if (gaps.Count > 0)
                notes.Add("extremos cercanos que NO se encadenan porque entre sus caras no hay hormigon comun (una columna u otro elemento " +
                          "unido con prioridad los separa): cada tramo termina en su cara y se arma aparte: " + string.Join(", ", gaps.Distinct()));
            var used = new HashSet<(int, bool)>();
            var next = new Dictionary<(int, bool), (int, bool)>();   // extremo -> extremo enlazado
            foreach (var l in links)
            {
                if (used.Contains((l.i, l.ei)) || used.Contains((l.j, l.ej))) continue;
                if (l.turn > 170 * Math.PI / 180) continue;   // un tramo que vuelve sobre si mismo no es una esquina
                used.Add((l.i, l.ei)); used.Add((l.j, l.ej));
                next[(l.i, l.ei)] = (l.j, l.ej);
                next[(l.j, l.ej)] = (l.i, l.ei);
            }

            // recorrer las cadenas desde un extremo libre (o desde cualquier tramo si es un anillo cerrado)
            var visited = new HashSet<int>();
            var order = new List<(int i, bool reversed)>();
            int number = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int start = 0; start < n; start++)
                {
                    if (visited.Contains(start)) continue;
                    bool freeStart = !next.ContainsKey((start, false)), freeEnd = !next.ContainsKey((start, true));
                    if (pass == 0 && !freeStart && !freeEnd) continue;   // en la primera pasada solo cadenas abiertas
                    var chain = new FootingChain { Number = ++number };
                    int cur = start;
                    // si solo el final es libre, se recorre al reves para que la cadena empiece en el extremo libre
                    bool reversed = !freeStart && freeEnd;
                    while (true)
                    {
                        visited.Add(cur);
                        order.Add((cur, reversed));
                        chain.Segs.Add(segs[cur]);
                        if (!next.TryGetValue((cur, !reversed), out var to)) break;   // el extremo por el que salimos
                        if (visited.Contains(to.Item1)) break;   // anillo cerrado
                        cur = to.Item1;
                        reversed = to.Item2;   // si entramos por su final, ese tramo va al reves
                    }
                    chains.Add(chain);
                }

            // tramos al reves: releer con el eje invertido, para que u, v, w sigan el recorrido
            foreach ((int i, bool reversed) in order)
            {
                if (!reversed) continue;
                HostAnalysis it = segs[i].Item;
                BeamSection flipped = BeamSection.Probe(doc, it.Host, cfg, -it.Section.DirW, it.Clip, it.Piece);
                if (flipped == null) { notes.Add(it.Tag + "no se pudo releer con el eje invertido: " + BeamSection.LastError); continue; }
                it.Section = flipped;
                ChainSeg re = Make(it);
                segs[i].Start = re.Start; segs[i].End = re.End;
            }

            foreach (FootingChain c in chains)
            {
                double w = 0;
                foreach (ChainSeg s in c.Segs) { s.W0 = w; w += s.Length; }
                if (c.Segs.Count > 1)
                    notes.Add("cadena " + c.Number + ": " + c.Segs.Count + " tramos encadenados (" + string.Join(" > ", c.Segs.Select(s => s.Item.Label)) +
                              "), recorrido " + (w * 0.3048).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " m");
            }
            return chains;
        }

        private static ChainSeg Make(HostAnalysis it)
        {
            BeamSection s = it.Section;
            Rect web = s.Profile.WebAt(0.5 * s.Length);
            return new ChainSeg { Item = it, Start = s.World(web.CU, web.CV, 0), End = s.World(web.CU, web.CV, s.Length) };
        }

        /// <summary>
        /// True si el trozo de eje que va de "reach" antes del punto p a "reach" despues (en la
        /// direccion d, hacia fuera del tramo) tiene alguna parte dentro del hormigon de la
        /// otra seccion: los dos tramos se tocan (esquina a inglete, uno que llega hasta la cara
        /// exterior del otro, dos alineados cara con cara). Si no se puede comprobar, true
        /// (se enlazan como siempre).
        /// </summary>
        private static bool Enters(XYZ p, XYZ d, BeamSection other, double reach)
        {
            try
            {
                Line probe = Line.CreateBound(p - d * reach, p + d * reach);
                var opt = new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside };
                foreach (Solid solid in other.AllSolids())
                {
                    if (solid == null) continue;
                    SolidCurveIntersection ix = solid.IntersectWithCurve(probe, opt);
                    if (ix == null) continue;
                    for (int i = 0; i < ix.SegmentCount; i++)
                        if (ix.GetCurveSegment(i).Length > 1e-6) return true;
                }
                return false;
            }
            catch { return true; }
        }
    }
}
