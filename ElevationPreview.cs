using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FootingRebar
{
    /// <summary>
    /// Esquema del alzado: el perfil del alma a lo largo de la viga (tramos constantes,
    /// cartelas y escalones), las barras corridas siguiendo las caras (prolongaciones en
    /// los apoyos a trazos, patillas), los bastones en su tramo y una raya por cada
    /// estribo, con la etiqueta de cada tramo de la distribucion ("inicio 1@50",
    /// "resto R@200 (=187)"). La escala vertical se exagera si hace falta para que se
    /// vea el canto.
    /// </summary>
    public sealed class ElevationPreview : Canvas
    {
        private BeamSection _s;
        private BeamPlan _plan;
        private List<StirrupRun> _runs;
        private List<BastonRange> _bastones;
        private AppConfig _cfg;
        private string _message = "Sin elemento armable";
        private const double FtToMm = 304.8;

        public ElevationPreview()
        {
            Background = Brushes.White;
            ClipToBounds = true;
            SizeChanged += (s, e) => Redraw();
        }

        public void Show(BeamSection s, BeamPlan plan, List<StirrupRun> runs, List<BastonRange> bastones, AppConfig cfg)
        {
            _s = s; _plan = plan; _runs = runs; _bastones = bastones ?? new List<BastonRange>(); _cfg = cfg;
            Redraw();
        }

        public void Clear(string message)
        {
            _s = null; _runs = null; _message = message;
            Redraw();
        }

        private static string Mm(double ft) => Math.Round(ft * FtToMm).ToString(CultureInfo.InvariantCulture);

        private void Redraw()
        {
            Children.Clear();
            double W = ActualWidth, H = ActualHeight;
            if (W < 10 || H < 10) return;
            if (_s == null || _runs == null || _cfg == null) { Text(_message, 10, 10, Brushes.Gray, 12); return; }

            BeamProfile prof = _s.Profile;
            double L = prof.Length;
            double ext0 = Math.Max(0, _cfg.Longitudinal.StartExtensionMm) / FtToMm;
            double ext1 = Math.Max(0, _cfg.Longitudinal.EndExtensionMm) / FtToMm;
            double leg = Math.Max(0, _cfg.Longitudinal.LegMm) / FtToMm;
            double anchor = _bastones.Count == 0 ? 0 : Math.Max(0, Math.Max(-_bastones.Min(b => b.W0), _bastones.Max(b => b.W1) - L));
            double left = Math.Max(ext0, anchor), right = Math.Max(ext1, anchor);
            double total = L + left + right;
            double vLo = prof.VMin, vHi = prof.VMax;
            double depth = Math.Max(vHi - vLo, 1e-6);
            double marginX = 30, marginTop = 30, marginBottom = 72;
            double kx = (W - 2 * marginX) / Math.Max(total, 1e-6);
            double ky = Math.Min((H - marginTop - marginBottom) / (depth + 2 * leg + 1e-6), 4 * kx);
            double x0 = marginX + left * kx;                     // x de la cara de inicio
            double yBase = H - marginBottom - Math.Max(leg, 0) * ky;   // y de vLo
            Func<double, double> X = w => x0 + w * kx;
            Func<double, double> Y = v => yBase - (v - vLo) * ky;

            // hormigon: contorno del alma tramo a tramo
            var poly = new Polygon { Fill = SectionPreview.ConcreteBrush, Stroke = Brushes.DimGray, StrokeThickness = 1.2 };
            foreach (ProfileSegment seg in prof.Segments)
            {
                poly.Points.Add(new Point(X(seg.W0), Y(seg.Web0.V2)));
                poly.Points.Add(new Point(X(seg.W1), Y(seg.Web1.V2)));
            }
            foreach (ProfileSegment seg in Enumerable.Reverse(prof.Segments))
            {
                poly.Points.Add(new Point(X(seg.W1), Y(seg.Web1.V1)));
                poly.Points.Add(new Point(X(seg.W0), Y(seg.Web0.V1)));
            }
            poly.ToolTip = prof.Describe();
            Children.Add(poly);
            // limites entre tramos
            foreach (ProfileSegment seg in prof.Segments.Skip(1))
            {
                Children.Add(new Line { X1 = X(seg.W0), Y1 = Y(vHi) - 6, X2 = X(seg.W0), Y2 = Y(vLo) + 6, Stroke = Brushes.Gray, StrokeThickness = 0.8, StrokeDashArray = new DoubleCollection { 3, 3 } });
                Text(Mm(seg.W0), X(seg.W0) - 12, Y(vHi) - 20, Brushes.Gray, 9);
            }
            Text("inicio", X(0) - 30, Y(vHi) - 16, Brushes.DimGray, 10);
            Text("fin  " + (L * 0.3048).ToString("0.00", CultureInfo.InvariantCulture) + " m", X(L) - 8, Y(vHi) - 16, Brushes.DimGray, 10);
            if (ky / kx > 1.05) Text("escala vertical x" + (ky / kx).ToString("0.#", CultureInfo.InvariantCulture), W - 110, 4, Brushes.Gray, 9);

            // estribos
            Brush sb = SectionPreview.StirrupBrush;
            double cover = _cfg.CoverMm / FtToMm;
            int runIdx = 0;
            foreach (StirrupRun run in _runs)
            {
                int lvl = runIdx++ % 3;   // las etiquetas de tramos cortos se escalonan en tres alturas para que no se pisen
                foreach (double w in run.Stations())
                {
                    Rect r = prof.WebAt(w);
                    Children.Add(new Line { X1 = X(w), Y1 = Y(r.V2 - cover), X2 = X(w), Y2 = Y(r.V1 + cover), Stroke = sb, StrokeThickness = 1.2 });
                }
                // llave del tramo bajo la viga
                double xa = X(run.W0), xb = X(run.W0 + run.Length);
                double yb = Y(vLo) + 16 + lvl * 13;
                Children.Add(new Line { X1 = xa, Y1 = yb, X2 = xb, Y2 = yb, Stroke = sb, StrokeThickness = 1 });
                Children.Add(new Line { X1 = xa, Y1 = yb - 4, X2 = xa, Y2 = yb, Stroke = sb, StrokeThickness = 1 });
                Children.Add(new Line { X1 = xb, Y1 = yb - 4, X2 = xb, Y2 = yb, Stroke = sb, StrokeThickness = 1 });
                var t = Text(run.Label + (run.Count > 1 ? "  x" + run.Count : ""), 0.5 * (xa + xb) - 30, yb + 2, sb, 9);
                t.ToolTip = run.Label + ": " + run.Count + " estribos desde w=" + Mm(run.W0) + " mm cada " + Mm(run.Spacing) + " mm";
            }

            // barras corridas y bastones: una trayectoria por fila (misma cara, capa y distancia a la cara)
            if (_plan != null && _plan.Error == null)
            {
                double endCover = _cfg.Longitudinal.EndCoverMm / FtToMm;
                double tol = _cfg.PrismCheckToleranceMm / FtToMm;
                // etiquetas de empalme ya puestas (cara + posicion + longitud) y cuantas lleva cada cara, para escalonarlas
                var spliceLabels = new HashSet<string>();
                var spliceRows = new Dictionary<string, int>();
                foreach ((PlanBar first, int count, double step) in _plan.ArrayRows(tol))
                {
                    Brush brush = SectionPreview.BrushOf(first);
                    double thick = first.IsBaston ? 2.6 : 1.8;
                    double inset = RebarGenerator.JogInset(_cfg, _plan.Ds, first.Db);
                    if (first.IsBaston)
                    {
                        foreach (BastonRange br in _bastones.Where(b => b.Index == first.Baston))
                        {
                            var path = BarPaths.Path(prof, first.Top, first.FaceOffset, br.W0, br.W1, inset, tol, null, first.Label);
                            DrawPath(path, X, Y, brush, thick, L, first.Label + " " + br.Label + ": " + count + " x " + first.TypeName);
                            double wm = 0.5 * (Math.Max(0, br.W0) + Math.Min(L, br.W1));
                            double vm = first.Top ? prof.WebAt(wm).V2 : prof.WebAt(wm).V1;
                            Text("B" + (first.Baston + 1) + " " + count + "x" + first.TypeName, X(wm) - 20, first.Top ? Y(vm) - 16 : Y(vm) + 2, brush, 9, true);
                        }
                    }
                    else
                    {
                        double w0 = ext0 > 0 ? -ext0 : endCover, w1 = ext1 > 0 ? L + ext1 : L - endCover;
                        var full = BarPaths.Path(prof, first.Top, first.FaceOffset, w0, w1, inset, tol, null, first.Label);
                        // empalmes por longitud comercial: cada trozo con su bayoneta, como los crea el generador
                        SplicedBar spl = RebarGenerator.SpliceFor(_s, _cfg, first, w0, w1, tol);
                        List<List<(double w, double v)>> pieces = RebarGenerator.PiecePaths(full, spl, first, _plan.Web.CV, tol);
                        string tip = first.Label + ": " + count + " x " + first.TypeName +
                                     (spl.Spliced ? " (" + pieces.Count + " trozos empalmados " + Mm(spl.Lap) + " mm)" : "");
                        foreach (var piece in pieces) DrawPath(piece, X, Y, brush, thick, L, tip);
                        var path = pieces[0];
                        var last = pieces[pieces.Count - 1];
                        double dir = first.Top ? -1 : 1;
                        if (leg > 0 && _cfg.Longitudinal.LegAtStart)
                            Children.Add(new Line { X1 = X(path[0].w), Y1 = Y(path[0].v), X2 = X(path[0].w), Y2 = Y(path[0].v + dir * leg), Stroke = brush, StrokeThickness = thick });
                        if (leg > 0 && _cfg.Longitudinal.LegAtEnd)
                        {
                            var e = last[last.Count - 1];
                            Children.Add(new Line { X1 = X(e.w), Y1 = Y(e.v), X2 = X(e.w), Y2 = Y(e.v + dir * leg), Stroke = brush, StrokeThickness = thick });
                        }
                        string faceKey = first.IsSide ? "L" : first.Top ? "S" : "I";
                        foreach (double cw in spl.Centers)
                        {
                            string key = faceKey + "|" + Math.Round(cw * 10) + "|" + Mm(spl.Lap);
                            if (!spliceLabels.Add(key)) continue;
                            spliceRows.TryGetValue(faceKey, out int row);
                            spliceRows[faceKey] = row + 1;
                            Rect rc = prof.WebAt(cw);
                            double y = first.IsSide ? Y(BarPaths.VAt(full, cw)) - 14 - row * 11 : first.Top ? Y(rc.V2) - 28 - row * 11 : Y(rc.V1) + 14 + row * 11;
                            var t = Text("empalme " + Mm(spl.Lap), X(cw) - 28, y, brush, 9, true);
                            t.ToolTip = first.Label + ": empalme por traslape de " + Mm(spl.Lap) + " mm centrado en w=" + Mm(cw) + " mm (" +
                                        Mm(cw - 0.5 * spl.Lap) + " a " + Mm(cw + 0.5 * spl.Lap) + ")";
                            Children.Add(new Line { X1 = X(cw - 0.5 * spl.Lap), Y1 = y + 12, X2 = X(cw + 0.5 * spl.Lap), Y2 = y + 12, Stroke = brush, StrokeThickness = 1 });
                        }
                    }
                }
                if (ext0 > 0) Text("-" + Mm(ext0) + " mm" + (leg > 0 && _cfg.Longitudinal.LegAtStart ? " + patilla " + Mm(leg) : ""), X(-ext0), Y(vHi) - 30, SectionPreview.CornerBrush, 9);
                if (ext1 > 0) Text("+" + Mm(ext1) + " mm" + (leg > 0 && _cfg.Longitudinal.LegAtEnd ? " + patilla " + Mm(leg) : ""), X(L) - 10, Y(vHi) - 30, SectionPreview.CornerBrush, 9);
            }

            int n = _runs.Sum(r => r.Count);
            Text(n + " estribos", 8, 4, Brushes.DimGray, 11);
        }

        /// <summary>Dibuja la trayectoria; los tramos fuera de la viga (w &lt; 0 o w &gt; L) van a trazos.</summary>
        private void DrawPath(List<(double w, double v)> path, Func<double, double> X, Func<double, double> Y, Brush brush, double thick, double L, string tip)
        {
            for (int i = 0; i + 1 < path.Count; i++)
            {
                var a = path[i]; var b = path[i + 1];
                bool outside = b.w <= 1e-9 || a.w >= L - 1e-9;
                var ln = new Line { X1 = X(a.w), Y1 = Y(a.v), X2 = X(b.w), Y2 = Y(b.v), Stroke = brush, StrokeThickness = thick, ToolTip = tip };
                if (outside) ln.StrokeDashArray = new DoubleCollection { 3, 2 };
                Children.Add(ln);
            }
        }

        private TextBlock Text(string s, double x, double y, Brush brush, double size, bool bold = false)
        {
            var t = new TextBlock { Text = s, Foreground = brush, FontSize = size };
            if (bold) t.FontWeight = FontWeights.SemiBold;
            SetLeft(t, x); SetTop(t, y);
            Children.Add(t);
            return t;
        }
    }
}
