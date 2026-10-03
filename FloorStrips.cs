using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace StripFootingRebar
{
    /// <summary>
    /// Un cimiento corrido modelado como suelo (Floor): el suelo no tiene curva de ubicacion,
    /// asi que el eje se deduce de su contorno en planta, y si el contorno tiene esquinas
    /// (una L, una U, un anillo cerrado, un cruce...) se parte en tramos rectos, cada uno
    /// un prisma de recorte del solido del suelo. Los tramos se encadenan despues por sus
    /// extremos como si fueran cimientos separados.
    /// </summary>
    public static class FloorStrips
    {
        /// <summary>Un tramo recto del suelo: el prisma vertical que lo recorta del solido y la direccion de su eje.</summary>
        public sealed class Strip
        {
            public Solid Clip;
            public XYZ Axis;
            /// <summary>Largo y ancho en planta (pies).</summary>
            public double Length, Width;
        }

        private static double Mm(double mm) => BeamSection.Mm(mm);
        private static string ToM(double ft) => (ft * 0.3048).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Parte el suelo en tramos rectos. Null con el motivo si el contorno no se puede leer
        /// (bordes curvos, suelo inclinado, contorno no rectilineo).
        /// </summary>
        public static List<Strip> Split(Floor floor, AppConfig cfg, out string error, out List<string> notes)
        {
            error = null;
            notes = new List<string>();
            List<Solid> solids = BeamSection.Solids(floor);
            if (solids.Count == 0) { error = "el suelo no tiene geometria solida"; return null; }
            Solid solid = solids[0];

            PlanarFace top = TopFace(solid);
            if (top == null) { error = "el suelo no tiene una cara superior horizontal (suelo inclinado): solo se arman suelos horizontales"; return null; }

            // contornos en planta (exterior y huecos) como listas de puntos
            var loops = new List<List<XYZ>>();
            foreach (CurveLoop loop in top.GetEdgesAsCurveLoops())
            {
                var pts = new List<XYZ>();
                foreach (Curve c in loop)
                {
                    if (!(c is Line))
                    {
                        error = "el contorno del suelo tiene bordes curvos: solo se arman tramos rectos (divide el suelo en tramos rectos)";
                        return null;
                    }
                    XYZ p = c.GetEndPoint(0);
                    pts.Add(new XYZ(p.X, p.Y, 0));
                }
                if (pts.Count >= 3) loops.Add(pts);
            }
            if (loops.Count == 0) { error = "no se pudo leer el contorno del suelo"; return null; }

            // sistema en planta: a = direccion dominante de los bordes, b = perpendicular
            XYZ a = DominantDirection(loops);
            XYZ b = XYZ.BasisZ.CrossProduct(a).Normalize();
            XYZ o = loops[0][0];
            double tol = Mm(cfg.PrismCheckToleranceMm);
            double angleTol = cfg.RectilinearAngleDeg * Math.PI / 180;

            var local = new List<List<Pt>>();
            foreach (List<XYZ> loop in loops)
            {
                List<Pt> l = Rectilinear.Simplify(loop.Select(p => new Pt((p - o).DotProduct(a), (p - o).DotProduct(b))).ToList(), tol);
                if (l.Count < 4) continue;
                if (!Rectilinear.IsRectilinear(l, angleTol, out string bad))
                {
                    error = "el contorno del suelo no es rectilineo (" + bad + "): los tramos tienen que ser rectos y perpendiculares entre si " +
                            "(divide el suelo en tramos rectos)";
                    return null;
                }
                local.Add(Rectilinear.Simplify(Rectilinear.Snap(l, tol), tol));
            }
            if (local.Count == 0) { error = "el contorno del suelo no tiene vertices suficientes"; return null; }

            // reticula de coordenadas de los vertices; una celda esta dentro si lo esta en un numero impar de contornos
            List<double> us = Rectilinear.Cluster(local.SelectMany(l => l.Select(p => p.U)), tol);
            List<double> vs = Rectilinear.Cluster(local.SelectMany(l => l.Select(p => p.V)), tol);
            int nu = us.Count - 1, nv = vs.Count - 1;
            if (nu < 1 || nv < 1) { error = "el contorno del suelo no tiene area"; return null; }
            var inside = new bool[nu, nv];
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    var center = new Pt(0.5 * (us[i] + us[i + 1]), 0.5 * (vs[j] + vs[j + 1]));
                    bool ins = false;
                    foreach (List<Pt> l in local) if (Rectilinear.Inside(l, center)) ins = !ins;
                    inside[i, j] = ins;
                }

            // rectangulos: primero a lo largo de a y luego a lo largo de b; se queda la particion con menos tramos
            List<(int i1, int j1, int i2, int j2)> rows = MergeCells(inside, nu, nv);
            var insideT = new bool[nv, nu];
            for (int i = 0; i < nu; i++) for (int j = 0; j < nv; j++) insideT[j, i] = inside[i, j];
            List<(int i1, int j1, int i2, int j2)> cols = MergeCells(insideT, nv, nu).Select(r => (i1: r.j1, j1: r.i1, i2: r.j2, j2: r.i2)).ToList();
            List<(int i1, int j1, int i2, int j2)> cells = cols.Count < rows.Count ? cols : rows;

            // extension vertical del solido, con holgura
            double z0 = double.MaxValue, z1 = double.MinValue;
            foreach (Edge ed in solid.Edges)
                foreach (XYZ p in ed.Tessellate()) { z0 = Math.Min(z0, p.Z); z1 = Math.Max(z1, p.Z); }

            double minSide = Mm(100);
            var strips = new List<Strip>();
            foreach ((int i1, int j1, int i2, int j2) in cells)
            {
                double u1 = us[i1], u2 = us[i2], v1 = vs[j1], v2 = vs[j2];
                double w = u2 - u1, h = v2 - v1;
                if (Math.Min(w, h) < minSide)
                {
                    notes.Add("se ignora un retal del contorno de " + BeamSection.ToMm(Math.Min(w, h)) + " x " + BeamSection.ToMm(Math.Max(w, h)) +
                              " mm (menos de 100 mm de ancho)");
                    continue;
                }
                // el prisma se ensancha "tol" para no dejar caras coincidentes en la operacion booleana
                var loop = new CurveLoop();
                XYZ p1 = Plan(o, a, b, u1 - tol, v1 - tol, z0 - 1), p2 = Plan(o, a, b, u2 + tol, v1 - tol, z0 - 1);
                XYZ p3 = Plan(o, a, b, u2 + tol, v2 + tol, z0 - 1), p4 = Plan(o, a, b, u1 - tol, v2 + tol, z0 - 1);
                loop.Append(Line.CreateBound(p1, p2));
                loop.Append(Line.CreateBound(p2, p3));
                loop.Append(Line.CreateBound(p3, p4));
                loop.Append(Line.CreateBound(p4, p1));
                Solid clip = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, z1 - z0 + 2);
                strips.Add(new Strip { Clip = clip, Axis = w >= h ? a : b, Length = Math.Max(w, h), Width = Math.Min(w, h) });
            }
            if (strips.Count == 0) { error = "no se encontro ningun tramo recto en el contorno del suelo"; return null; }

            // los tramos van ordenados por su posicion en planta para que el listado sea estable
            strips = strips.OrderBy(s => Math.Round(s.Clip.ComputeCentroid().DotProduct(b), 3)).ThenBy(s => s.Clip.ComputeCentroid().DotProduct(a)).ToList();
            if (strips.Count > 1)
                notes.Add("suelo partido en " + strips.Count + " tramos rectos: " +
                          string.Join(", ", strips.Select(s => ToM(s.Length) + " x " + ToM(s.Width) + " m")));
            return strips;
        }

        private static XYZ Plan(XYZ o, XYZ a, XYZ b, double u, double v, double z)
        {
            XYZ p = o + a * u + b * v;
            return new XYZ(p.X, p.Y, z);
        }

        /// <summary>
        /// Une las celdas interiores en rectangulos: en cada fila (j) las celdas consecutivas
        /// forman un rectangulo, y un rectangulo se prolonga a la fila siguiente si en ella
        /// hay exactamente el mismo intervalo de columnas. Indices de celda (i1..i2, j1..j2).
        /// </summary>
        private static List<(int i1, int j1, int i2, int j2)> MergeCells(bool[,] inside, int nu, int nv)
        {
            var rects = new List<(int i1, int j1, int i2, int j2)>();
            for (int j = 0; j < nv; j++)
            {
                int i = 0;
                while (i < nu)
                {
                    if (!inside[i, j]) { i++; continue; }
                    int i2 = i;
                    while (i2 < nu && inside[i2, j]) i2++;
                    int k = rects.FindIndex(r => r.i1 == i && r.i2 == i2 && r.j2 == j);
                    if (k >= 0) rects[k] = (i, rects[k].j1, i2, j + 1);
                    else rects.Add((i, j, i2, j + 1));
                    i = i2;
                }
            }
            return rects;
        }

        /// <summary>Cara superior horizontal mas grande del solido (null si no hay).</summary>
        private static PlanarFace TopFace(Solid solid)
        {
            PlanarFace best = null;
            foreach (Face f in solid.Faces)
                if (f is PlanarFace pf && pf.FaceNormal.Z > 0.99 && (best == null || pf.Area > best.Area)) best = pf;
            return best;
        }

        /// <summary>Direccion horizontal (unitaria) en la que mas longitud de borde hay, contando los bordes paralelos y los perpendiculares.</summary>
        private static XYZ DominantDirection(List<List<XYZ>> loops)
        {
            var edges = new List<(XYZ dir, double len)>();
            foreach (List<XYZ> loop in loops)
                for (int i = 0; i < loop.Count; i++)
                {
                    XYZ d = loop[(i + 1) % loop.Count] - loop[i];
                    double len = d.GetLength();
                    if (len > 1e-6) edges.Add((d / len, len));
                }
            XYZ best = XYZ.BasisX;
            double bestSum = -1, bestLen = -1;
            foreach ((XYZ dir, double len) in edges)
            {
                double sum = 0;
                foreach ((XYZ d2, double l2) in edges)
                {
                    double dot = Math.Abs(dir.DotProduct(d2));
                    if (dot > 0.9999 || dot < 0.0001) sum += l2;
                }
                if (sum > bestSum + 1e-9 || (Math.Abs(sum - bestSum) <= 1e-9 && len > bestLen)) { bestSum = sum; bestLen = len; best = dir; }
            }
            // la direccion se toma con componente X positiva (o Y positiva si es vertical en planta) para que sea estable
            if (best.X < -1e-9 || (Math.Abs(best.X) <= 1e-9 && best.Y < 0)) best = -best;
            return best;
        }

        /// <summary>Direccion dominante del contorno superior de un solido de suelo (para el eje cuando no se parte en tramos).</summary>
        public static XYZ DominantDirection(Solid solid)
        {
            PlanarFace top = TopFace(solid);
            if (top == null) return null;
            var loops = new List<List<XYZ>>();
            foreach (CurveLoop loop in top.GetEdgesAsCurveLoops())
            {
                var pts = new List<XYZ>();
                foreach (Curve c in loop) { XYZ p = c.GetEndPoint(0); pts.Add(new XYZ(p.X, p.Y, 0)); }
                if (pts.Count >= 3) loops.Add(pts);
            }
            return loops.Count == 0 ? null : DominantDirection(loops);
        }
    }
}
