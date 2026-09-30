using System;
using System.Collections.Generic;
using System.Linq;

namespace FootingRebar
{
    /// <summary>Punto 2D en coordenadas locales de la seccion (u, v), en pies.</summary>
    public struct Pt
    {
        public double U, V;
        public Pt(double u, double v) { U = u; V = v; }
        public double DistanceTo(Pt o) => Math.Sqrt((U - o.U) * (U - o.U) + (V - o.V) * (V - o.V));
        public override string ToString() => "(" + U + ", " + V + ")";
    }

    /// <summary>Rectangulo alineado con los ejes locales (u1 &lt;= u2, v1 &lt;= v2), en pies.</summary>
    public sealed class Rect
    {
        public double U1, V1, U2, V2;
        public Rect(double u1, double v1, double u2, double v2) { U1 = u1; V1 = v1; U2 = u2; V2 = v2; }
        public double W => U2 - U1;
        public double H => V2 - V1;
        public double Area => W * H;
        public double CU => 0.5 * (U1 + U2);
        public double CV => 0.5 * (V1 + V2);

        public bool Contains(Rect o, double tol) =>
            o.U1 >= U1 - tol && o.U2 <= U2 + tol && o.V1 >= V1 - tol && o.V2 <= V2 + tol;

        public bool ContainsPoint(Pt p, double tol) =>
            p.U >= U1 - tol && p.U <= U2 + tol && p.V >= V1 - tol && p.V <= V2 + tol;

        /// <summary>Copia encogida "d" por cada lado (d puede ser negativo).</summary>
        public Rect Inset(double d) => new Rect(U1 + d, V1 + d, U2 - d, V2 - d);

        public Rect Clone() => new Rect(U1, V1, U2, V2);
    }

    /// <summary>
    /// Geometria pura (sin Revit) de poligonos rectilineos: comprobacion, limpieza y
    /// descomposicion en rectangulos maximos, que son los estribos cerrados de la
    /// columna (una L son dos rectangulos solapados, una T dos, una cruz dos, una U tres).
    /// Se puede probar fuera de Revit.
    /// </summary>
    public static class Rectilinear
    {
        /// <summary>Area con signo (positiva si el poligono va en sentido antihorario).</summary>
        public static double SignedArea(IList<Pt> poly)
        {
            double a = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                Pt p = poly[i], q = poly[(i + 1) % poly.Count];
                a += p.U * q.V - q.U * p.V;
            }
            return 0.5 * a;
        }

        /// <summary>Centroide del poligono (formula del area).</summary>
        public static Pt Centroid(IList<Pt> poly)
        {
            double a = 0, cu = 0, cv = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                Pt p = poly[i], q = poly[(i + 1) % poly.Count];
                double cross = p.U * q.V - q.U * p.V;
                a += cross;
                cu += (p.U + q.U) * cross;
                cv += (p.V + q.V) * cross;
            }
            if (Math.Abs(a) < 1e-12) return poly[0];
            a *= 0.5;
            return new Pt(cu / (6 * a), cv / (6 * a));
        }

        /// <summary>True si el punto esta dentro del poligono (rayo horizontal; los bordes cuentan como dentro con tolerancia).</summary>
        public static bool Inside(IList<Pt> poly, Pt p, double tol = 0)
        {
            if (tol > 0 && OnBoundary(poly, p, tol)) return true;
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Pt a = poly[i], b = poly[j];
                if ((a.V > p.V) != (b.V > p.V))
                {
                    double x = (b.U - a.U) * (p.V - a.V) / (b.V - a.V) + a.U;
                    if (p.U < x) inside = !inside;
                }
            }
            return inside;
        }

        /// <summary>True si el punto esta a menos de "tol" de algun borde.</summary>
        public static bool OnBoundary(IList<Pt> poly, Pt p, double tol)
        {
            for (int i = 0; i < poly.Count; i++)
                if (DistanceToSegment(p, poly[i], poly[(i + 1) % poly.Count]) <= tol) return true;
            return false;
        }

        public static double DistanceToSegment(Pt p, Pt a, Pt b)
        {
            double du = b.U - a.U, dv = b.V - a.V;
            double len2 = du * du + dv * dv;
            double t = len2 < 1e-18 ? 0 : Math.Max(0, Math.Min(1, ((p.U - a.U) * du + (p.V - a.V) * dv) / len2));
            return p.DistanceTo(new Pt(a.U + t * du, a.V + t * dv));
        }

        /// <summary>
        /// Limpia el poligono: quita vertices repetidos (a menos de "tol") y vertices
        /// intermedios de bordes colineales (Revit puede partir un borde en varios).
        /// </summary>
        public static List<Pt> Simplify(IList<Pt> poly, double tol)
        {
            var pts = new List<Pt>();
            foreach (Pt p in poly)
                if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(p) > tol) pts.Add(p);
            while (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) <= tol) pts.RemoveAt(pts.Count - 1);

            bool changed = true;
            while (changed && pts.Count > 3)
            {
                changed = false;
                for (int i = 0; i < pts.Count; i++)
                {
                    Pt a = pts[(i + pts.Count - 1) % pts.Count], b = pts[i], c = pts[(i + 1) % pts.Count];
                    double cross = (b.U - a.U) * (c.V - b.V) - (b.V - a.V) * (c.U - b.U);
                    double la = a.DistanceTo(b), lc = b.DistanceTo(c);
                    if (la < 1e-12 || lc < 1e-12 || Math.Abs(cross) / (la * lc) < 1e-6)
                    {
                        // b esta en la recta a-c: sobra, salvo que sea un "pico" (a-b y b-c en sentidos opuestos)
                        double dot = (b.U - a.U) * (c.U - b.U) + (b.V - a.V) * (c.V - b.V);
                        if (dot > 0 || la < 1e-12 || lc < 1e-12) { pts.RemoveAt(i); changed = true; break; }
                    }
                }
            }
            return pts;
        }

        /// <summary>
        /// True si todos los bordes son paralelos al eje u o al eje v (angulo maximo "angleTol"
        /// en radianes). "badEdge" describe el primer borde que no lo cumple.
        /// </summary>
        public static bool IsRectilinear(IList<Pt> poly, double angleTol, out string badEdge)
        {
            badEdge = null;
            for (int i = 0; i < poly.Count; i++)
            {
                Pt a = poly[i], b = poly[(i + 1) % poly.Count];
                double du = Math.Abs(b.U - a.U), dv = Math.Abs(b.V - a.V);
                double len = Math.Sqrt(du * du + dv * dv);
                if (len < 1e-12) continue;
                double ang = Math.Min(Math.Asin(Math.Min(1, dv / len)), Math.Asin(Math.Min(1, du / len)));
                if (ang > angleTol)
                {
                    badEdge = "borde " + (i + 1) + " inclinado " + Math.Round(ang * 180 / Math.PI, 1) + " grados";
                    return false;
                }
            }
            return true;
        }

        /// <summary>Ajusta cada vertice a la coordenada compartida (u o v) de los vecinos, para que los bordes queden exactos.</summary>
        public static List<Pt> Snap(IList<Pt> poly, double tol)
        {
            List<double> us = Cluster(poly.Select(p => p.U), tol);
            List<double> vs = Cluster(poly.Select(p => p.V), tol);
            return poly.Select(p => new Pt(Nearest(us, p.U), Nearest(vs, p.V))).ToList();
        }

        private static double Nearest(List<double> values, double x)
        {
            double best = values[0];
            foreach (double v in values) if (Math.Abs(v - x) < Math.Abs(best - x)) best = v;
            return best;
        }

        /// <summary>Valores unicos ordenados, agrupando los que difieren menos de "tol" (se toma su media).</summary>
        public static List<double> Cluster(IEnumerable<double> values, double tol)
        {
            var sorted = values.OrderBy(x => x).ToList();
            var result = new List<double>();
            int i = 0;
            while (i < sorted.Count)
            {
                int j = i;
                double sum = 0;
                while (j < sorted.Count && sorted[j] - sorted[i] <= tol) { sum += sorted[j]; j++; }
                result.Add(sum / (j - i));
                i = j;
            }
            return result;
        }

        /// <summary>
        /// Rectangulos maximos inscritos en el poligono rectilineo: todo rectangulo formado
        /// por celdas de la reticula de coordenadas de los vertices que este entero dentro
        /// del poligono y no este contenido en otro. De mayor a menor area.
        /// </summary>
        public static List<Rect> MaximalRectangles(IList<Pt> poly, double tol)
        {
            List<double> us = Cluster(poly.Select(p => p.U), tol);
            List<double> vs = Cluster(poly.Select(p => p.V), tol);
            int nu = us.Count - 1, nv = vs.Count - 1;
            if (nu < 1 || nv < 1) return new List<Rect>();

            var inside = new bool[nu, nv];
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                    inside[i, j] = Inside(poly, new Pt(0.5 * (us[i] + us[i + 1]), 0.5 * (vs[j] + vs[j + 1])));

            var candidates = new List<Rect>();
            for (int i1 = 0; i1 < nu; i1++)
                for (int i2 = i1 + 1; i2 <= nu; i2++)
                    for (int j1 = 0; j1 < nv; j1++)
                        for (int j2 = j1 + 1; j2 <= nv; j2++)
                        {
                            bool full = true;
                            for (int i = i1; i < i2 && full; i++)
                                for (int j = j1; j < j2; j++)
                                    if (!inside[i, j]) { full = false; break; }
                            if (full) candidates.Add(new Rect(us[i1], vs[j1], us[i2], vs[j2]));
                        }

            var maximal = new List<Rect>();
            foreach (Rect r in candidates)
            {
                bool contained = candidates.Any(o => !ReferenceEquals(o, r) && o.Area > r.Area + 1e-12 && o.Contains(r, tol));
                if (!contained && !maximal.Any(m => m.Contains(r, tol) && r.Contains(m, tol))) maximal.Add(r);
            }
            return maximal.OrderByDescending(r => r.Area).ThenBy(r => r.V1).ThenBy(r => r.U1).ToList();
        }

        /// <summary>Nombre de la forma segun vertices y rectangulos: rectangular, en L, en T, en cruz, en U o en Z, o poligonal.</summary>
        public static string Kind(IList<Pt> poly, IList<Rect> rects)
        {
            int n = poly.Count, r = rects.Count;
            if (n == 4 && r == 1) return "rectangular";
            if (n == 6 && r == 2) return "en L";
            if (n == 8 && r == 2) return "en T";
            if (n == 12 && r == 2) return "en cruz";
            if (n == 8 && r == 3)
            {
                // U: dos rectangulos (los brazos) comparten un lado con el tercero (la base) y
                // estan en el mismo lado; Z: los brazos estan en lados opuestos
                Rect b = rects[0];
                bool sameSide = true;
                var sides = new List<int>();
                foreach (Rect o in rects.Skip(1))
                {
                    if (o.CU < b.U1 - 1e-9) sides.Add(0); else if (o.CU > b.U2 + 1e-9) sides.Add(1);
                    else if (o.CV < b.V1 - 1e-9) sides.Add(2); else if (o.CV > b.V2 + 1e-9) sides.Add(3);
                    else sides.Add(-1);
                }
                sameSide = sides.Count == 2 && sides[0] == sides[1] && sides[0] >= 0;
                return sameSide ? "en U" : "en Z";
            }
            return "poligonal (" + n + " vertices)";
        }
    }
}
