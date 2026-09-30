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
    /// Esquema pequeno de los bastones solos: el alzado de la viga (perfil del alma) con
    /// cada baston en su tramo, arriba o abajo, con su etiqueta. Sin las barras corridas
    /// ni los estribos, para ver de un vistazo donde queda cada uno.
    /// </summary>
    public sealed class BastonPreview : Canvas
    {
        private BeamSection _s;
        private BeamPlan _plan;
        private List<BastonRange> _bastones;
        private AppConfig _cfg;
        private const double FtToMm = 304.8;

        public BastonPreview()
        {
            Background = Brushes.White;
            ClipToBounds = true;
            SizeChanged += (s, e) => Redraw();
        }

        public void Show(BeamSection s, BeamPlan plan, List<BastonRange> bastones, AppConfig cfg)
        {
            _s = s; _plan = plan; _bastones = bastones ?? new List<BastonRange>(); _cfg = cfg;
            Redraw();
        }

        public void Clear()
        {
            _s = null; _bastones = null;
            Redraw();
        }

        private static string Mm(double ft) => Math.Round(ft * FtToMm).ToString(CultureInfo.InvariantCulture);

        private void Redraw()
        {
            Children.Clear();
            double W = ActualWidth, H = ActualHeight;
            if (W < 10 || H < 10) return;
            if (_s == null || _bastones == null) { Text("Sin elemento armable", 8, 8, Brushes.Gray, 11); return; }
            BeamProfile prof = _s.Profile;
            double L = prof.Length;
            double anchor = _bastones.Count == 0 ? 0 : Math.Max(0, Math.Max(-_bastones.Min(b => b.W0), _bastones.Max(b => b.W1) - L));
            double total = L + 2 * anchor;
            double vLo = prof.VMin, vHi = prof.VMax, depth = Math.Max(vHi - vLo, 1e-6);
            double mx = 24, mt = 26, mb = 26;
            double kx = (W - 2 * mx) / Math.Max(total, 1e-6);
            double ky = Math.Min((H - mt - mb) / depth, 4 * kx);
            double x0 = mx + anchor * kx;
            double yBase = H - mb;
            Func<double, double> X = w => x0 + w * kx;
            Func<double, double> Y = v => yBase - (v - vLo) * ky;

            var poly = new Polygon { Fill = SectionPreview.ConcreteBrush, Stroke = Brushes.DimGray, StrokeThickness = 1 };
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
            Children.Add(poly);
            Text("inicio", X(0) - 12, yBase + 2, Brushes.DimGray, 9);
            Text("fin", X(L) - 6, yBase + 2, Brushes.DimGray, 9);

            if (_bastones.Count == 0) { Text("Sin bastones", 8, 6, Brushes.Gray, 10); return; }
            double cover = _cfg.CoverMm / FtToMm;
            int k = 0;
            foreach (BastonRange br in _bastones.OrderBy(b => b.Index).ThenBy(b => b.W0))
            {
                bool top = br.Cfg.IsTop;
                // varias filas de bastones de la misma cara se escalonan un poco para que se distingan
                int stack = br.Index % 3;
                double off = cover + (_plan?.Ds ?? 0) + stack * (0.5 * depth / 6);
                var pts = new PointCollection();
                double wa = Math.Max(br.W0, -anchor), wb = Math.Min(br.W1, L + anchor);
                int n = 12;
                for (int i = 0; i <= n; i++)
                {
                    double w = wa + (wb - wa) * i / n;
                    Rect r = prof.WebAt(w);
                    pts.Add(new Point(X(w), Y(top ? r.V2 - off : r.V1 + off)));
                }
                var line = new Polyline
                {
                    Points = pts, Stroke = SectionPreview.BastonBrush, StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round,
                    ToolTip = "Baston " + (br.Index + 1) + " (" + br.Cfg.Describe + "): " + br.Cfg.Count + " x " + br.Cfg.BarTypeName +
                              ", de w=" + Mm(br.W0) + " a " + Mm(br.W1) + " mm"
                };
                Children.Add(line);
                double wm = 0.5 * (wa + wb);
                Point pm = pts[n / 2];
                Text("B" + (br.Index + 1) + " " + br.Cfg.Count + "x" + br.Cfg.BarTypeName + " " + Mm(wb - wa), X(wm) - 28, top ? pm.Y - 15 : pm.Y + 3, SectionPreview.BastonBrush, 9, true);
                k++;
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
