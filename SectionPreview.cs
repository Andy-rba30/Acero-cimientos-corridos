using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FootingRebar
{
    /// <summary>
    /// Esquema de la seccion de la viga con su armado: hormigon (la seccion de referencia,
    /// la mas cercana al centro del vano), el estribo del alma con sus ganchos, cada capa
    /// con su etiqueta (S1, S2... arriba; I1, I2... abajo) y cada barra a su diametro
    /// (rojo oscuro las extremas, naranja las intermedias, morado los bastones). Zoom con
    /// la rueda (centrado en el cursor), desplazamiento arrastrando y doble clic para
    /// volver a encajar. Toda la geometria sale de BeamPlan, la misma clase que usa el
    /// generador.
    /// </summary>
    public sealed class SectionPreview : Canvas
    {
        private BeamSection _s;
        private BeamPlan _plan;
        private double _hookDeg;
        private string _message = "Sin elemento armable";
        /// <summary>Claves de las barras seleccionadas con clic (para asignarles otro tipo).</summary>
        public HashSet<string> Selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public event Action SelectionChanged;
        private bool _clickedBar;

        private double _zoom = 1;
        private Vector _pan;
        private double _x0, _y0;
        private Point _dragStart;
        private Vector _panStart;
        private bool _dragging;

        private const double FtToMm = 304.8;

        public static readonly Brush StirrupBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0x7A));
        public static readonly Brush CornerBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x2E, 0x2E));
        public static readonly Brush IntermediateBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x6C, 0x2A));
        public static readonly Brush BastonBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x3E, 0x9D));
        public static readonly Brush ConcreteBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));

        public static readonly Brush SideBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x6F, 0xB6));
        public static Brush BrushOf(PlanBar b) => b.IsSide ? SideBrush : b.IsBaston ? BastonBrush : b.Kind == BarKind.Corner ? CornerBrush : IntermediateBrush;

        public SectionPreview()
        {
            Background = Brushes.White;
            ClipToBounds = true;
            SizeChanged += (s, e) => Redraw();
            MouseWheel += OnWheel;
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseLeave += (s, e) => { _dragging = false; ReleaseMouseCapture(); };
            Cursor = Cursors.Hand;
        }

        /// <param name="hookDeg">Angulo del gancho de los estribos en grados (90, 135, 180); 0 = sin gancho.</param>
        public void Show(BeamSection s, BeamPlan plan, double hookDeg)
        {
            bool changed = !ReferenceEquals(_s, s);
            _s = s; _plan = plan; _hookDeg = hookDeg;
            if (changed) ResetView(); else Redraw();
        }

        public void Clear(string message)
        {
            _s = null; _plan = null; _message = message;
            Redraw();
        }

        public void ResetView()
        {
            _zoom = 1; _pan = new Vector(0, 0);
            Redraw();
        }

        // --- zoom y desplazamiento ---
        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (_s == null) return;
            double factor = e.Delta > 0 ? 1.25 : 1 / 1.25;
            double newZoom = Math.Max(1, Math.Min(40, _zoom * factor));
            factor = newZoom / _zoom;
            Point m = e.GetPosition(this);
            _pan = new Vector(m.X - _x0 - (m.X - _pan.X - _x0) * factor, m.Y - _y0 - (m.Y - _pan.Y - _y0) * factor);
            _zoom = newZoom;
            if (_zoom <= 1.0001) _pan = new Vector(0, 0);
            Redraw();
            e.Handled = true;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (_s == null) return;
            if (_clickedBar) { _clickedBar = false; return; }
            if (e.ClickCount == 2) { ResetView(); e.Handled = true; return; }
            _dragging = true; _dragStart = e.GetPosition(this); _panStart = _pan;
            CaptureMouse();
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            _pan = _panStart + (e.GetPosition(this) - _dragStart);
            Redraw();
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            ReleaseMouseCapture();
        }

        // --- dibujo ---
        private static string Mm(double ft) => Math.Round(ft * FtToMm).ToString(CultureInfo.InvariantCulture);

        private void Redraw()
        {
            Children.Clear();
            double W = ActualWidth, H = ActualHeight;
            if (W < 10 || H < 10) return;
            if (_s == null || _plan == null)
            {
                Text(_message, 10, 10, Brushes.Gray, 12);
                return;
            }

            BeamProfile prof = _s.Profile;
            ProfileStation st = prof.Reference;
            double uMin = prof.UMin, vMin = prof.VMin;
            double width = Math.Max(prof.Width, 1e-6), depth = Math.Max(prof.Depth, 1e-6);
            double margin = 50;
            double k = Math.Min((W - 2 * margin) / width, (H - 2 * margin) / depth) * _zoom;
            // origen sin zoom (esquina inferior izquierda); el zoom crece desde ahi y el desplazamiento se suma
            _x0 = 0.5 * (W - width * k / _zoom);
            _y0 = 0.5 * (H + depth * k / _zoom);
            double x0 = _x0 + _pan.X, y0 = _y0 + _pan.Y;
            Func<double, double> X = u => x0 + (u - uMin) * k;
            Func<double, double> Y = v => y0 - (v - vMin) * k;

            // hormigon de la seccion de referencia
            var poly = new Polygon { Fill = ConcreteBrush, Stroke = Brushes.DimGray, StrokeThickness = 1.2 };
            foreach (Pt p in st.Polygon) poly.Points.Add(new Point(X(p.U), Y(p.V)));
            poly.ToolTip = "Seccion a " + Mm(st.W) + " mm de la cara de inicio";
            Children.Add(poly);

            Rect web = st.Web;
            // cotas del alma
            Text(Mm(web.W) + " mm", X(web.CU) - 25, Y(web.V1) + 18, Brushes.DimGray, 11);
            Text(Mm(web.H) + " mm", X(prof.UMax) + 6, Y(web.CV) - 8, Brushes.DimGray, 11);
            if (Math.Abs(prof.Width - web.W) > 1e-9)
                Text("ala " + Mm(prof.Width) + " mm", X(uMin), Y(prof.VMax) - 16, Brushes.DimGray, 10);

            if (_plan.Error != null)
            {
                Text(_plan.Error, 10, 10, Brushes.Firebrick, 12);
                return;
            }

            // estribo (a su tamano en esta seccion)
            Rect line = web.Inset(_plan.Cover + 0.5 * _plan.Ds);
            double th = Math.Max(1.5, _plan.Ds * k);
            var r = new Rectangle
            {
                Stroke = StirrupBrush, StrokeThickness = th, Fill = null, StrokeLineJoin = PenLineJoin.Round,
                Width = Math.Max(1, line.W * k), Height = Math.Max(1, line.H * k),
                ToolTip = "Estribo " + Mm(line.W + _plan.Ds) + " x " + Mm(line.H + _plan.Ds) + " mm (recubrimiento " + Mm(_plan.Cover) + " mm)"
            };
            SetLeft(r, X(line.U1)); SetTop(r, Y(line.V2));
            Children.Add(r);

            // ganchos en la esquina superior izquierda (donde empieza y acaba el estribo)
            if (_hookDeg > 0)
            {
                double hook = Math.Max(6 * _plan.Ds, 75 / FtToMm);
                double gap = 1.2 * _plan.Ds;
                var c = new Pt(line.U1, line.V2);
                HookLeg(X, Y, new Pt(c.U, c.V - gap), -1, 0, 0, -1, _hookDeg, hook, StirrupBrush, th);
                HookLeg(X, Y, new Pt(c.U + gap, c.V), 0, 1, 1, 0, _hookDeg, hook, StirrupBrush, th);
            }

            // barras (cada una con su diametro, en la posicion que tienen en esta seccion)
            foreach (PlanBar bar in _plan.Bars)
            {
                double rr = Math.Max(2.5, 0.5 * bar.Db * k);
                Pt p = bar.P(web);
                var e = new Ellipse
                {
                    Width = 2 * rr, Height = 2 * rr, Fill = BrushOf(bar),
                    Stroke = Selected.Contains(bar.Key) ? Brushes.Gold : bar.Assigned ? Brushes.Black : Brushes.Black,
                    StrokeThickness = Selected.Contains(bar.Key) ? 2.5 : bar.Assigned ? 1.8 : 0.6,
                    Cursor = Cursors.Arrow,
                    ToolTip = bar.Label + ": " + bar.TypeName + " (" + Mm(bar.Db) + " mm)" + (bar.Assigned ? " [tipo asignado en esta viga]" : "") +
                              " en u=" + Mm(p.U) + ", v=" + Mm(p.V) + " mm; a " + Mm(bar.FaceOffset) + " mm de la cara " + (bar.Top ? "superior" : "inferior") +
                              ". Clic: seleccionar / quitar de la seleccion"
                };
                string key = bar.Key;
                e.MouseLeftButtonDown += (sn, ev) =>
                {
                    _clickedBar = true;
                    if (!Selected.Remove(key)) Selected.Add(key);
                    ev.Handled = true;
                    _clickedBar = false;
                    Redraw();
                    SelectionChanged?.Invoke();
                };
                SetLeft(e, X(p.U) - rr); SetTop(e, Y(p.V) - rr);
                Children.Add(e);
            }

            // etiquetas de las capas (izquierda)
            foreach (PlanLayer l in _plan.Layers)
            {
                if (l.Bars.Count == 0) continue;
                double v = l.Bars.Average(b => b.V(web));
                Text(l.Name, X(uMin) - 24, Y(v) - 8, l.Top ? CornerBrush : IntermediateBrush, 10, true);
            }
            foreach (var g in _plan.Bars.Where(b => b.IsSide).GroupBy(b => b.Layer))
                Text("L" + g.Key, X(uMin) - 24, Y(g.First().V(web)) - 8, SideBrush, 10, true);

            // resumen
            Text(_plan.Describe() + " | " + _plan.DescribeLayers(), 8, H - 20, Brushes.DimGray, 11);
            if (_plan.Warnings.Count > 0) Text(string.Join(" | ", _plan.Warnings), 8, H - 36, Brushes.Firebrick, 11);
            if (prof.Variable)
                Text("seccion a " + Mm(st.W) + " mm del inicio (canto " + Mm(web.H) + " mm; minimo " + Mm(prof.MinDepth) + ", maximo " + Mm(prof.MaxDepth) + ")",
                     8, 8, Brushes.DimGray, 10);
        }

        /// <summary>
        /// Pata de un gancho en el punto "at": la barra llega con direccion (du, dv) y dobla
        /// "deg" grados hacia el interior (nu, nv): 90 = la pata sigue el interior, 135 = a 45
        /// grados hacia el nucleo, 180 = vuelve sobre la barra (separada un diametro).
        /// </summary>
        private void HookLeg(Func<double, double> X, Func<double, double> Y, Pt at, double du, double dv, double nu, double nv,
                             double deg, double len, Brush brush, double thickness)
        {
            double a = deg * Math.PI / 180;
            double lu = Math.Cos(a) * du + Math.Sin(a) * nu, lv = Math.Cos(a) * dv + Math.Sin(a) * nv;
            Pt from = at;
            if (deg >= 170) from = new Pt(at.U + nu * 1.5 * _plan.Ds, at.V + nv * 1.5 * _plan.Ds);
            Children.Add(new Line
            {
                X1 = X(from.U), Y1 = Y(from.V), X2 = X(from.U + lu * len), Y2 = Y(from.V + lv * len),
                Stroke = brush, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
        }

        private TextBlock Text(string s, double x, double y, Brush brush, double size, bool bold = false)
        {
            var t = new TextBlock { Text = s, Foreground = brush, FontSize = size, TextWrapping = TextWrapping.NoWrap };
            if (bold) t.FontWeight = FontWeights.SemiBold;
            SetLeft(t, x); SetTop(t, y);
            Children.Add(t);
            return t;
        }
    }
}
