using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Document = Autodesk.Revit.DB.Document;

namespace FootingRebar
{
    /// <summary>
    /// Ventana previa al armado: muestra que se ha detectado en cada viga seleccionada
    /// (forma de la seccion, tramos, longitud, o el motivo del rechazo) y deja elegir el
    /// armado: capas de barras corridas de cada cara (con dos diametros por capa),
    /// bastones, prolongaciones y patillas, distribucion de estribos (global y por
    /// elemento), ganchos y recubrimiento, con un esquema de la seccion y del alzado que
    /// se redibuja con cada cambio. Los valores iniciales vienen de config.json y se pueden
    /// guardar como nuevos valores por defecto. Construida en codigo (sin XAML), con el
    /// mismo estilo que el add-in de columnas.
    /// </summary>
    public sealed class RebarOptionsWindow : System.Windows.Window
    {
        private readonly Document _doc;
        private readonly AppConfig _cfg;
        private readonly IList<string> _barTypes;
        private readonly IDictionary<string, double> _diametersMm;
        private readonly Dictionary<string, string> _typeByDisplay = new Dictionary<string, string>();
        private readonly IList<string> _hookTypes;
        private readonly IDictionary<string, double> _hookAngles;
        private readonly IList<HostAnalysis> _items;

        /// <summary>Configuracion final si el usuario pulso "Armar"; null si cancelo.</summary>
        public AppConfig Result { get; private set; }

        // --- longitudinales ---
        private sealed class LayerRow
        {
            public LayerCfg Cfg;
            public ComboBox Corner, Inter;
            public TextBox Count;
        }
        private readonly Dictionary<bool, List<LayerCfg>> _layerStore = new Dictionary<bool, List<LayerCfg>>();
        private readonly Dictionary<bool, List<LayerRow>> _layerRows = new Dictionary<bool, List<LayerRow>>();
        private readonly Dictionary<bool, Grid> _layerGrid = new Dictionary<bool, Grid>();
        private readonly Dictionary<bool, TextBlock> _layerSummary = new Dictionary<bool, TextBlock>();
        private readonly Dictionary<bool, Button> _layerAdd = new Dictionary<bool, Button>();
        private TextBox _longStart, _longEnd, _endCover, _leg, _layerClear, _minClear, _sidePairs;
        private ComboBox _sideType;
        private CheckBox _legStart, _legEnd, _anchorAdjacent;

        // --- barras por capa de la viga seleccionada ---
        // --- seleccion especial de barras (tipo asignado barra a barra en la viga seleccionada) ---
        private TextBlock _selCaption, _selList;
        private ComboBox _selType;
        private Button _selAssign, _selClear;

        // --- bastones ---
        private sealed class BastonRow
        {
            public BastonCfg Cfg;
            public ComboBox Face, Position, Type, Stack;
            public TextBox Count, Gap, Length, Anchor, From, To;
        }
        private readonly List<BastonCfg> _bastonStore = new List<BastonCfg>();
        private readonly List<BastonRow> _bastonRows = new List<BastonRow>();
        private Grid _bastonGrid;
        private TextBlock _bastonMessage;

        // --- estribos y general ---
        private ComboBox _stType, _stHook, _stOrient, _joined;
        private TextBox _stDist, _stStartOff, _stEndOff, _cover, _partition;
        private CheckBox _stSym;
        private TextBlock _message, _partitionPreview, _previewCaption;
        private Button _buildButton;
        private SectionPreview _preview;
        private ElevationPreview _elevation;
        private BastonPreview _bastonPreview;

        private readonly Dictionary<HostAnalysis, (System.Windows.Documents.Run kind, System.Windows.Documents.Run detail)> _itemRuns
            = new Dictionary<HostAnalysis, (System.Windows.Documents.Run, System.Windows.Documents.Run)>();
        private readonly Dictionary<HostAnalysis, Border> _itemRows = new Dictionary<HostAnalysis, Border>();
        /// <summary>Marca de agua de la caja de distribucion propia de cada viga (muestra la general).</summary>
        private readonly Dictionary<HostAnalysis, TextBlock> _distHints = new Dictionary<HostAnalysis, TextBlock>();
        private HostAnalysis _selected;
        private bool _building = true;
        private bool _strictTypes;

        private const string NoHook = "(sin gancho)";
        private const string SameAsCorner = "(igual que las extremas)";
        private static readonly Thickness Pad = new Thickness(4, 2, 4, 2);
        private static readonly Brush SelectedBrush = RevitTheme.Selection;

        public RebarOptionsWindow(Document doc, AppConfig cfg, IList<string> barTypes, IDictionary<string, double> diametersMm,
                                  IList<string> hookTypes, IDictionary<string, double> hookAngles, IList<HostAnalysis> items)
        {
            _doc = doc;
            _hookAngles = hookAngles ?? new Dictionary<string, double>();
            _cfg = cfg;
            _cfg.Normalize();
            _diametersMm = diametersMm;
            _barTypes = barTypes.OrderBy(n => diametersMm.TryGetValue(n, out double mm) ? mm : 0)
                                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string n in _barTypes) _typeByDisplay[TypeDisplay(n)] = n;
            _hookTypes = hookTypes;
            _items = items;
            foreach (bool top in new[] { true, false })
            {
                _layerStore[top] = _cfg.Face(top).Layers.Select(l => l.Clone()).ToList();
                _layerRows[top] = new List<LayerRow>();
            }
            foreach (BastonCfg b in _cfg.Bastones) _bastonStore.Add(b.Clone());

            Title = "Armar cimientos / sobrecimientos";
            Width = 1240;
            Height = 860;
            MinWidth = 1000;
            MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            FontSize = 12;

            RevitTheme.Apply(this);
            Content = BuildRoot();
            _selected = _items.FirstOrDefault(i => i.CanBuild);
            if (_selected != null) SelectItem(_selected);
            _building = false;
            Refresh();
        }

        // ------------------------------------------------------------------
        // Construccion de la interfaz
        // ------------------------------------------------------------------
        private UIElement BuildRoot()
        {
            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            UIElement elements = BuildElements();
            Grid.SetRow(elements, 0);
            root.Children.Add(elements);

            var body = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new StackPanel();
            left.Children.Add(BuildLongitudinal());
            left.Children.Add(BuildBastones());
            left.Children.Add(BuildStirrups());
            left.Children.Add(BuildGeneral());
            var scroll = new ScrollViewer
            {
                Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(scroll, 0);
            body.Children.Add(scroll);

            UIElement previews = BuildPreviews();
            Grid.SetColumn(previews, 1);
            body.Children.Add(previews);

            Grid.SetRow(body, 1);
            root.Children.Add(body);

            UIElement buttons = BuildButtons();
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);
            return root;
        }

        private UIElement BuildElements()
        {
            int ok = _items.Count(i => i.CanBuild);
            var group = new GroupBox
            {
                Header = "Cimientos / sobrecimientos / recorridos: " + _items.Count + " (" + ok + " armables). Los tramos que se tocan por los extremos forman un recorrido. Haz clic en uno para verlo. " +
                         "A la derecha, la distribucion de estribos propia de cada recorrido (vacio = la general).",
                Padding = new Thickness(4)
            };
            var panel = new StackPanel();
            foreach (HostAnalysis item in _items)
            {
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                text.Inlines.Add(new System.Windows.Documents.Run(item.Tag) { FontWeight = FontWeights.Bold });
                var kindRun = new System.Windows.Documents.Run(item.Kind + ": ")
                {
                    FontWeight = FontWeights.SemiBold,
                    Foreground = item.CanBuild ? RevitTheme.Ok : RevitTheme.Error
                };
                var detailRun = new System.Windows.Documents.Run(item.Detail(_cfg));
                text.Inlines.Add(kindRun);
                text.Inlines.Add(detailRun);
                _itemRuns[item] = (kindRun, detailRun);
                Grid.SetColumn(text, 0);
                row.Children.Add(text);

                var side = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                side.Children.Add(new TextBlock { Text = "Estribos de este recorrido:", Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                var dist = new TextBox
                {
                    Width = 170, Text = item.DistributionOverride, Background = Brushes.Transparent,
                    ToolTip = "Distribucion de estribos propia de esta viga, como \"1@50, 10@100, R@200\" (por ejemplo, mas estribos en " +
                              "una viga mas cargada). Vacio = se usa la distribucion general del apartado Estribos."
                };
                var hint = new TextBlock { Foreground = RevitTheme.Hint, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
                var box = new Grid { Width = 170 };
                box.Children.Add(new Border { Background = RevitTheme.Input });
                box.Children.Add(hint);
                box.Children.Add(dist);
                _distHints[item] = hint;
                HostAnalysis captured = item;
                dist.TextChanged += (s, e) => { captured.DistributionOverride = dist.Text; hint.Visibility = dist.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden; Refresh(); };
                side.Children.Add(box);
                Grid.SetColumn(side, 1);
                row.Children.Add(side);
                side.Visibility = item.CanBuild ? Visibility.Visible : Visibility.Collapsed;

                var border = new Border { Child = row, Padding = new Thickness(4, 2, 4, 2), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand };
                border.MouseLeftButtonDown += (s, e) => { SelectItem(captured); Refresh(); };
                _itemRows[item] = border;
                panel.Children.Add(border);
            }
            group.Content = new ScrollViewer
            {
                Content = panel, MaxHeight = 150,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            return group;
        }

        private void SelectItem(HostAnalysis item)
        {
            if (!ReferenceEquals(_selected, item) && _preview != null) _preview.Selected.Clear();
            _selected = item;
            foreach (var kv in _itemRows)
                kv.Value.Background = kv.Key == item ? SelectedBrush : Brushes.Transparent;
        }

        private UIElement BuildLongitudinal()
        {
            var group = new GroupBox { Header = "Barras longitudinales corridas (por capas, de fuera hacia dentro)", Padding = new Thickness(4) };
            var panel = new StackPanel();

            foreach (bool top in new[] { true, false })
            {
                panel.Children.Add(new TextBlock
                {
                    Text = top ? "Cara superior" : "Cara inferior", FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(4, top ? 2 : 8, 4, 2)
                });
                var grid = new Grid { Margin = new Thickness(4, 0, 4, 0) };
                foreach (double w in new[] { 52, 170, 190, 54, 60 })
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
                _layerGrid[top] = grid;
                panel.Children.Add(grid);
                var foot = new DockPanel { Margin = new Thickness(4, 0, 4, 0) };
                var add = new Button { Content = "Anadir capa", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left };
                bool captured = top;
                add.Click += (s, e) =>
                {
                    ReadLayerRows(captured, null);
                    if (_layerStore[captured].Count >= 3) return;
                    LayerCfg last = _layerStore[captured][_layerStore[captured].Count - 1].Clone();
                    last.Count = 2;
                    _layerStore[captured].Add(last);
                    _building = true; RebuildLayerTable(captured); _building = false;
                    Refresh();
                };
                _layerAdd[top] = add;
                DockPanel.SetDock(add, Dock.Left);
                foot.Children.Add(add);
                var summary = new TextBlock { Foreground = RevitTheme.Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                _layerSummary[top] = summary;
                foot.Children.Add(summary);
                panel.Children.Add(foot);
                RebuildLayerTable(top);
            }

            panel.Children.Add(new TextBlock { Text = "Capa intermedia: barras laterales (pares simetricos, una en cada costado)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 4, 2) });
            var sideRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 0) };
            sideRow.Children.Add(new TextBlock { Text = "Tipo de barra:", Margin = Pad, VerticalAlignment = VerticalAlignment.Center });
            _sideType = TypeCombo(_cfg.SideBars.BarTypeName);
            _sideType.Width = 170;
            _sideType.ToolTip = "Tipo de barra de las laterales (barras de alma), pegadas a las ramas del estribo.";
            sideRow.Children.Add(_sideType);
            sideRow.Children.Add(new TextBlock { Text = "Pares:", Margin = new Thickness(12, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center });
            _sidePairs = CountBox(_cfg.SideBars.Pairs);
            _sidePairs.ToolTip = "Numero de pares: cada par es una barra a cada costado, a la misma altura. Se reparten por igual en el canto libre entre las capas superiores e inferiores. 0 = sin laterales. En el centro de la seccion no va nada.";
            sideRow.Children.Add(_sidePairs);
            Hook(_sideType); Hook(_sidePairs);
            panel.Children.Add(sideRow);

            var form = FormGrid();
            int r = 0;
            _longStart = NumBox(_cfg.Longitudinal.StartExtensionMm);
            AddRow(form, r++, "Prolongacion en el inicio (mm):", _longStart,
                   "Cuanto sobresalen las barras corridas mas alla de la cara de inicio de la viga (anclaje en el apoyo). 0 = terminan en el recubrimiento del extremo.");
            _longEnd = NumBox(_cfg.Longitudinal.EndExtensionMm);
            AddRow(form, r++, "Prolongacion en el fin (mm):", _longEnd, "Idem en la cara final de la viga.");
            _endCover = NumBox(_cfg.Longitudinal.EndCoverMm);
            AddRow(form, r++, "Recubrimiento en extremos (mm):", _endCover, "Donde terminan las barras (corridas y bastones sin anclaje) cuando no se prolongan mas alla de la cara.");
            _anchorAdjacent = new CheckBox { Content = "Anclar en el cimiento al que llega (T o esquina)", IsChecked = _cfg.Longitudinal.AnchorInAdjacent, Margin = Pad };
            AddRow(form, r++, "Extremos contra otro cimiento:", _anchorAdjacent,
                   "En un extremo sin prolongacion que llega a otro cimiento (en T, en una esquina que no sigue el recorrido o en la que cierra " +
                   "un anillo), las barras corridas y los bastones atraviesan ese cimiento y terminan en su cara opuesta, menos el recubrimiento " +
                   "de extremos, en vez de quedarse en la cara. Solo si el hormigon contiguo termina a menos de 2 m (o dos anchos).");
            var legRow = new StackPanel { Orientation = Orientation.Horizontal };
            _leg = NumBox(_cfg.Longitudinal.LegMm);
            _legStart = new CheckBox { Content = "en el inicio", IsChecked = _cfg.Longitudinal.LegAtStart, Margin = Pad, VerticalAlignment = VerticalAlignment.Center };
            _legEnd = new CheckBox { Content = "en el fin", IsChecked = _cfg.Longitudinal.LegAtEnd, Margin = Pad, VerticalAlignment = VerticalAlignment.Center };
            legRow.Children.Add(_leg); legRow.Children.Add(_legStart); legRow.Children.Add(_legEnd);
            Hook(_leg); Hook(_legStart); Hook(_legEnd);
            AddRow(form, r++, "Patilla a 90 grados (mm):", legRow,
                   "Patilla en los extremos prolongados o anclados en el cimiento contiguo: las barras superiores doblan hacia abajo y las " +
                   "inferiores hacia arriba (gancho estandar dentro del apoyo). En un extremo sin prolongacion ni anclaje no se pone, y las " +
                   "barras que doblan en las esquinas de un recorrido van sin patilla. 0 = sin patilla.");
            _layerClear = NumBox(_cfg.Longitudinal.LayerClearMm);
            AddRow(form, r++, "Separacion libre entre capas (mm):", _layerClear, "Hueco libre entre una capa y la siguiente de la misma cara (25 mm o un diametro segun norma).");
            _minClear = NumBox(_cfg.Longitudinal.MinClearMm);
            AddRow(form, r++, "Separacion libre minima (mm):", _minClear, "Hueco libre minimo entre barras de una misma capa (ademas nunca menor que un diametro). Si no se cumple, se avisa.");
            form.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(form);

            _selCaption = new TextBlock { Margin = new Thickness(4, 8, 4, 2), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(_selCaption);
            var selRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 0) };
            _selType = TypeCombo("");
            _selType.Width = 190;
            _selType.ToolTip = "Tipo de barra que se asigna a las barras seleccionadas en el esquema de la seccion (solo en esta viga)";
            selRow.Children.Add(_selType);
            _selAssign = new Button { Content = "Asignar a la seleccion", Padding = new Thickness(8, 2, 8, 2), Margin = Pad };
            _selAssign.Click += (s, e) =>
            {
                if (_selected == null || !_selected.CanBuild) return;
                string t = TypeOf(_selType);
                if (t.Length == 0) { _message.Foreground = RevitTheme.Error; _message.Text = "Elige el tipo de barra que quieres asignar a las barras seleccionadas."; return; }
                foreach (string key in _preview.Selected) _selected.BarTypeOverrides[key] = t;
                _preview.Selected.Clear();
                Refresh();
            };
            selRow.Children.Add(_selAssign);
            _selClear = new Button { Content = "Quitar asignacion", Padding = new Thickness(8, 2, 8, 2), Margin = Pad, ToolTip = "Las barras seleccionadas vuelven al tipo general de su capa (sin seleccion: todas las de esta viga)" };
            _selClear.Click += (s, e) =>
            {
                if (_selected == null || !_selected.CanBuild) return;
                if (_preview.Selected.Count == 0) _selected.BarTypeOverrides.Clear();
                else foreach (string key in _preview.Selected) _selected.BarTypeOverrides.Remove(key);
                _preview.Selected.Clear();
                Refresh();
            };
            selRow.Children.Add(_selClear);
            panel.Children.Add(selRow);
            _selList = new TextBlock { Foreground = RevitTheme.Muted, Margin = new Thickness(4, 2, 4, 2), TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(_selList);
            panel.Children.Add(new TextBlock
            {
                Text = "Haz clic en las barras del esquema de la seccion (se marcan en amarillo), elige un tipo y pulsa \"Asignar a la seleccion\": esas barras " +
                       "llevan ese diametro solo en esta viga. Repite con otra seleccion para usar varios diametros. Las barras con tipo asignado se dibujan con borde grueso.",
                TextWrapping = TextWrapping.Wrap, Foreground = RevitTheme.Muted, Margin = new Thickness(4, 2, 4, 0)
            });
            group.Content = panel;
            return group;
        }

        private void RebuildLayerTable(bool top)
        {
            Grid grid = _layerGrid[top];
            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            _layerRows[top].Clear();
            string[] headers = { "Capa", "Barras extremas", "Barras intermedias", "Total", "" };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < headers.Length; c++)
            {
                var h = new TextBlock { Text = headers[c], Foreground = RevitTheme.Muted, Margin = Pad };
                Grid.SetRow(h, 0); Grid.SetColumn(h, c);
                grid.Children.Add(h);
            }
            List<LayerCfg> store = _layerStore[top];
            for (int i = 0; i < store.Count; i++)
            {
                LayerCfg cfg = store[i];
                int r = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var row = new LayerRow { Cfg = cfg };
                var name = new TextBlock
                {
                    Text = (top ? "S" : "I") + (i + 1), Margin = Pad, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold,
                    ToolTip = i == 0 ? "Capa pegada al estribo: sus dos barras extremas son las esquinas" : "Capa " + (i + 1) + ", apilada por dentro de la anterior con la separacion libre entre capas"
                };
                Put(grid, name, r, 0);
                row.Corner = TypeCombo(cfg.BarTypeName);
                row.Corner.ToolTip = "Tipo de barra de las dos barras extremas de la capa (en la capa 1, las esquinas del estribo).";
                Put(grid, row.Corner, r, 1);
                row.Inter = TypeCombo(cfg.IntermediateBarTypeName);
                row.Inter.Items.Insert(0, SameAsCorner);
                if (RebarGenerator.MatchName(_barTypes, cfg.IntermediateBarTypeName) == null) row.Inter.SelectedIndex = 0;
                else row.Inter.SelectedIndex = row.Inter.SelectedIndex + 1;
                row.Inter.ToolTip = "Tipo de barra de las intermedias de la capa (las que van entre las dos extremas). Puede ser otro diametro: \"2 de 3/4 + 1 de 5/8\".";
                Put(grid, row.Inter, r, 2);
                row.Count = CountBox(cfg.Count);
                row.Count.ToolTip = i == 0 ? "Total de barras de la capa, extremas incluidas (minimo 2)" : "Total de barras de la capa (0 = sin capa)";
                Put(grid, row.Count, r, 3);
                Hook(row.Corner); Hook(row.Inter); Hook(row.Count);
                if (i > 0)
                {
                    var remove = new Button { Content = "Quitar", Padding = new Thickness(6, 1, 6, 1), Margin = Pad };
                    LayerCfg captured = cfg;
                    remove.Click += (s, e) =>
                    {
                        ReadLayerRows(top, null);
                        _layerStore[top].Remove(captured);
                        _building = true; RebuildLayerTable(top); _building = false;
                        Refresh();
                    };
                    Put(grid, remove, r, 4);
                }
                _layerRows[top].Add(row);
            }
            if (_layerAdd.TryGetValue(top, out Button add)) add.IsEnabled = store.Count < 3;
        }

        /// <summary>Vuelca la tabla de capas de una cara en su almacen. errors puede ser null (lectura tolerante).</summary>
        private void ReadLayerRows(bool top, List<string> errors)
        {
            int i = 0;
            foreach (LayerRow row in _layerRows[top])
            {
                i++;
                LayerCfg c = row.Cfg;
                c.BarTypeName = TypeOf(row.Corner);
                c.IntermediateBarTypeName = row.Inter.SelectedIndex <= 0 ? "" : TypeOf(row.Inter);
                string name = "capa " + (top ? "S" : "I") + i;
                int min = i == 1 ? 2 : 0;
                if (int.TryParse(row.Count.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= min)
                {
                    c.Count = n;
                    row.Count.ClearValue(Control.BorderBrushProperty);
                }
                else
                {
                    errors?.Add(name + ": numero de barras no valido (minimo " + min + ")");
                    row.Count.BorderBrush = RevitTheme.Error;
                }
            }
        }

        private static TextBox CountBox(int v) => new TextBox { Text = v.ToString(CultureInfo.InvariantCulture), Width = 44, Margin = Pad };

        /// <summary>
        /// Reconstruye (si cambia la viga o su numero de capas) o actualiza el cuadro de
        /// barras por capa de la viga seleccionada: una entrada por capa configurada de cada
        /// cara. Vacio = el general; con valor, esa viga lleva ese numero de barras en esa capa.
        /// </summary>

        /// <summary>Texto de la seleccion especial de barras de la viga seleccionada y de los tipos ya asignados.</summary>
        private void RefreshSelection(BeamPlan plan)
        {
            HostAnalysis item = _selected != null && _selected.CanBuild ? _selected : null;
            bool ok = item != null && plan != null && plan.Error == null;
            _selAssign.IsEnabled = ok && _preview.Selected.Count > 0;
            _selClear.IsEnabled = ok && item.BarTypeOverrides.Count > 0;
            if (!ok) { _selCaption.Text = "Seleccion especial de barras: selecciona un recorrido armable en la lista"; _selList.Text = ""; return; }
            _selCaption.Text = "Seleccion especial de barras de " + item.Tag.Trim() + ": " + _preview.Selected.Count + " barra(s) seleccionada(s) en el esquema de la seccion";
            var groups = plan.Bars.Where(b => b.Assigned).GroupBy(b => b.TypeName)
                             .Select(g => g.Count() + " x " + g.Key + " (" + string.Join(", ", g.Select(b => b.Label).Distinct().Take(4)) + (g.Select(b => b.Label).Distinct().Count() > 4 ? "..." : "") + ")");
            _selList.Text = item.BarTypeOverrides.Count == 0 ? "Sin tipos asignados a mano en esta viga." : "Asignados: " + string.Join(" | ", groups);
        }

        private UIElement BuildBastones()
        {
            var group = new GroupBox { Header = "Bastones (refuerzos cortos arriba o abajo: en los apoyos, en el centro o en un tramo)", Padding = new Thickness(4) };
            var panel = new StackPanel();
            _bastonGrid = new Grid();
            for (int c = 0; c < 11; c++)
                _bastonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.Children.Add(new ScrollViewer
            {
                Content = _bastonGrid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            });
            RebuildBastonTable();

            var add = new Button { Content = "Anadir baston", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 6, 4, 2), HorizontalAlignment = HorizontalAlignment.Left };
            add.Click += (s, e) =>
            {
                ReadBastonRows(null);
                BastonCfg last = _bastonStore.Count > 0 ? _bastonStore[_bastonStore.Count - 1].Clone() : new BastonCfg();
                if (_bastonStore.Count == 0 && _layerStore[true].Count > 0) last.BarTypeName = _layerStore[true][0].BarTypeName;
                _bastonStore.Add(last);
                _building = true; RebuildBastonTable(); _building = false;
                Refresh();
            };
            panel.Children.Add(add);
            _bastonMessage = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 4, 0), Foreground = RevitTheme.Error };
            panel.Children.Add(_bastonMessage);
            panel.Children.Add(new TextBlock
            {
                Text = "Cada baston va en la cara superior o inferior, apilado por dentro de las barras corridas de esa cara (tocandolas, " +
                       "o con el hueco indicado) o intercalado en la misma capa que ellas, en los huecos entre las corridas, de forma " +
                       "simetrica. Longitud en mm (en metros si es menor de 5): en inicio/fin/ambos se mide desde la cara del apoyo " +
                       "hacia el vano y el anclaje sigue dentro del apoyo; en centro es la longitud total centrada en el vano, o, si se " +
                       "escriben \"hacia inicio\" y \"hacia fin\", esas dos longitudes desde el centro del vano (armados especiales).",
                TextWrapping = TextWrapping.Wrap, Foreground = RevitTheme.Muted, Margin = new Thickness(4, 4, 4, 0)
            });
            group.Content = panel;
            return group;
        }

        private void RebuildBastonTable()
        {
            _bastonGrid.Children.Clear();
            _bastonGrid.RowDefinitions.Clear();
            _bastonRows.Clear();

            string[] headers = { "Cara", "Posicion", "Tipo de barra", "Barras", "Colocacion", "Hueco (mm)", "Longitud (mm)", "Anclaje (mm)", "Hacia inicio (mm)", "Hacia fin (mm)", "" };
            _bastonGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < headers.Length; c++)
            {
                var h = new TextBlock { Text = headers[c], Foreground = RevitTheme.Muted, Margin = Pad };
                Grid.SetRow(h, 0); Grid.SetColumn(h, c);
                _bastonGrid.Children.Add(h);
            }
            if (_bastonStore.Count == 0)
            {
                _bastonGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var none = new TextBlock { Text = "Sin bastones. Pulsa \"Anadir baston\" para crear uno.", Foreground = RevitTheme.Muted, Margin = Pad };
                Grid.SetRow(none, 1); Grid.SetColumn(none, 0); Grid.SetColumnSpan(none, 11);
                _bastonGrid.Children.Add(none);
            }

            int i = 0;
            foreach (BastonCfg cfg in _bastonStore)
            {
                i++;
                int r = _bastonGrid.RowDefinitions.Count;
                _bastonGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var row = new BastonRow { Cfg = cfg };

                row.Face = new ComboBox { Margin = Pad, MinWidth = 80, ToolTip = "Baston " + i };
                row.Face.Items.Add("Superior");
                row.Face.Items.Add("Inferior");
                row.Face.SelectedIndex = cfg.IsTop ? 0 : 1;
                Put(_bastonGrid, row.Face, r, 0);

                row.Position = new ComboBox { Margin = Pad, MinWidth = 120 };
                foreach (string l in BastonCfg.PositionLabels) row.Position.Items.Add(l);
                row.Position.SelectedIndex = cfg.PositionIndex;
                Put(_bastonGrid, row.Position, r, 1);

                row.Type = TypeCombo(cfg.BarTypeName);
                row.Type.MinWidth = 150;
                Put(_bastonGrid, row.Type, r, 2);

                row.Count = CountBox(cfg.Count);
                row.Count.ToolTip = "Numero de barras del baston en la seccion";
                Put(_bastonGrid, row.Count, r, 3);

                row.Stack = new ComboBox { Margin = Pad, MinWidth = 120 };
                row.Stack.Items.Add("Por dentro de las corridas (2a capa)");
                row.Stack.Items.Add("Entre las corridas (misma capa)");
                row.Stack.SelectedIndex = cfg.Stacked ? 0 : 1;
                row.Stack.ToolTip = "Apilado por dentro de las barras corridas de su cara (tocandolas o con el hueco indicado), " +
                                    "o intercalado en la misma capa, en los huecos entre las corridas";
                Put(_bastonGrid, row.Stack, r, 4);

                row.Gap = NumBox(cfg.GapMm);
                row.Gap.Width = 56;
                row.Gap.ToolTip = "Solo apilados: hueco entre el baston y las barras corridas. 0 = tocandolas";
                Put(_bastonGrid, row.Gap, r, 5);

                row.Length = new TextBox { Text = cfg.Length, Width = 64, Margin = Pad, ToolTip = "Longitud del baston en mm (en metros si es menor de 5)" };
                Put(_bastonGrid, row.Length, r, 6);

                row.Anchor = NumBox(cfg.AnchorMm);
                row.Anchor.Width = 64;
                row.Anchor.ToolTip = "Inicio/fin/ambos: cuanto sigue el baston dentro del apoyo, mas alla de la cara de la viga. 0 = empieza en el recubrimiento del extremo";
                Put(_bastonGrid, row.Anchor, r, 7);

                row.From = NumBox(cfg.FromMm);
                row.From.Width = 64;
                Put(_bastonGrid, row.From, r, 8);
                row.To = NumBox(cfg.ToMm);
                row.To.Width = 64;
                Put(_bastonGrid, row.To, r, 9);

                var remove = new Button { Content = "Quitar", Padding = new Thickness(8, 2, 8, 2), Margin = Pad };
                BastonCfg captured = cfg;
                remove.Click += (s, e) =>
                {
                    ReadBastonRows(null);
                    _bastonStore.Remove(captured);
                    _building = true; RebuildBastonTable(); _building = false;
                    Refresh();
                };
                Put(_bastonGrid, remove, r, 10);

                BastonRow rowRef = row;
                row.Position.SelectionChanged += (s, e) => UpdateBastonRowState(rowRef);
                row.Stack.SelectionChanged += (s, e) => UpdateBastonRowState(rowRef);
                foreach (FrameworkElement fe in new FrameworkElement[] { row.Face, row.Position, row.Type, row.Count, row.Stack, row.Gap, row.Length, row.Anchor, row.From, row.To })
                    Hook(fe);
                UpdateBastonRowState(row);
                _bastonRows.Add(row);
            }
        }

        private static void UpdateBastonRowState(BastonRow row)
        {
            int p = row.Position.SelectedIndex;
            bool ends = p == 0 || p == 1 || p == 2, custom = p == 3;
            row.Length.IsEnabled = true;
            row.From.ToolTip = "Solo centro: longitud desde el centro del vano hacia el inicio. Con las dos en 0 se usa la longitud total centrada";
            row.To.ToolTip = "Solo centro: longitud desde el centro del vano hacia el fin";
            row.Gap.IsEnabled = row.Stack.SelectedIndex == 0;
            row.Anchor.IsEnabled = ends;
            row.From.IsEnabled = custom;
            row.To.IsEnabled = custom;
        }

        /// <summary>Vuelca la tabla de bastones en su almacen. errors puede ser null (lectura tolerante); requireTypes = exigir tipo de barra.</summary>
        private void ReadBastonRows(List<string> errors, bool requireTypes = false)
        {
            int i = 0;
            foreach (BastonRow row in _bastonRows)
            {
                i++;
                BastonCfg c = row.Cfg;
                string name = "baston " + i;
                c.Face = row.Face.SelectedIndex == 1 ? "bottom" : "top";
                c.Position = BastonCfg.Positions[Math.Max(0, Math.Min(3, row.Position.SelectedIndex))];
                c.BarTypeName = TypeOf(row.Type);
                if (c.BarTypeName.Length == 0 && requireTypes) errors?.Add(name + ": elige un tipo de barra");
                c.Stacked = row.Stack.SelectedIndex == 0;
                c.GapMm = ReadNum(row.Gap, name + ": hueco", 0, row.Gap.IsEnabled ? errors : null);
                if (int.TryParse(row.Count.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1)
                { c.Count = n; row.Count.ClearValue(Control.BorderBrushProperty); }
                else { errors?.Add(name + ": numero de barras no valido"); row.Count.BorderBrush = RevitTheme.Error; }
                c.Length = row.Length.Text.Trim();
                if (row.Length.IsEnabled && !StirrupLayout.TryLength(c.Length, 1000, out _, out string lerr))
                { errors?.Add(name + ": " + lerr); row.Length.BorderBrush = RevitTheme.Error; }
                else row.Length.ClearValue(Control.BorderBrushProperty);
                c.AnchorMm = ReadNum(row.Anchor, name + ": anclaje", 0, row.Anchor.IsEnabled ? errors : null);
                c.FromMm = ReadNum(row.From, name + ": hacia inicio", 0, row.From.IsEnabled ? errors : null);
                c.ToMm = ReadNum(row.To, name + ": hacia fin", 0, row.To.IsEnabled ? errors : null);
                if (row.To.IsEnabled && (c.FromMm > 0) != (c.ToMm > 0)) { errors?.Add(name + ": escribe las dos longitudes (hacia inicio y hacia fin) o deja las dos en 0"); row.To.BorderBrush = RevitTheme.Error; }
            }
        }

        private UIElement BuildStirrups()
        {
            var group = new GroupBox { Header = "Estribos (uno rectangular cerrado en el alma)", Padding = new Thickness(4) };
            var grid = FormGrid();
            int r = 0;
            _stType = TypeCombo(_cfg.Stirrups.BarTypeName);
            AddRow(grid, r++, "Tipo de barra:", _stType, "Tipo de barra de los estribos.");
            _stHook = HookCombo(_cfg.Stirrups.HookTypeName);
            AddRow(grid, r++, "Gancho:", _stHook, "Tipo de gancho (RebarHookType) en los dos extremos del estribo, normalmente 135 grados. Sin gancho = estribo cerrado sin ganchos.");
            _stOrient = OrientCombo(_cfg.HookLeft);
            AddRow(grid, r++, "Giro del gancho:", _stOrient,
                   "Lado hacia el que giran los ganchos. Si con la orientacion elegida quedan fuera del hormigon, el plugin la invierte solo y lo avisa.");
            _stDist = new TextBox { Text = _cfg.Stirrups.Distribution, Margin = Pad };
            AddRow(grid, r++, "Distribucion desde cada apoyo:", _stDist,
                   "Como en los planos: \"1@50, 8@100, R@200\" = el primero a 50 mm de la cara del apoyo, ocho mas cada 100 mm y el resto cada 200 mm " +
                   "como maximo (repartidos por igual). Valores menores de 5 se leen en metros (\"1@.05\"). Cada viga puede tener la suya en la lista de arriba.");
            _stSym = new CheckBox { Content = "Repetir los grupos desde el otro apoyo (confinamiento en los dos extremos)", IsChecked = _cfg.Stirrups.Symmetric, Margin = Pad };
            AddRow(grid, r++, "", _stSym, "Con la casilla marcada, los grupos fijos (1@50, 8@100...) se colocan tambien desde el final en espejo y el resto va en medio.");
            _stStartOff = NumBox(_cfg.Stirrups.StartOffsetMm);
            AddRow(grid, r++, "Desfase en el inicio (mm):", _stStartOff, "La distribucion empieza a contar desde la cara de inicio mas este desfase (por ejemplo media columna si la viga esta modelada de eje a eje).");
            _stEndOff = NumBox(_cfg.Stirrups.EndOffsetMm);
            AddRow(grid, r++, "Desfase en el fin (mm):", _stEndOff, "La distribucion termina en la cara final menos este desfase.");
            group.Content = grid;
            return group;
        }

        private UIElement BuildGeneral()
        {
            var group = new GroupBox { Header = "Recubrimiento, geometria unida y particion", Padding = new Thickness(4) };
            var grid = FormGrid();
            int r = 0;
            _cover = NumBox(_cfg.CoverMm);
            AddRow(grid, r++, "Recubrimiento al estribo (mm):", _cover, "Distancia de cada cara del alma al borde exterior del estribo.");
            _joined = new ComboBox { Margin = Pad };
            _joined.Items.Add("Auto: seccion completa de la familia, longitud del solido cortado");
            _joined.Items.Add("Solo el solido cortado (tal como lo ve Revit)");
            _joined.Items.Add("Geometria completa de la familia (tambien en longitud)");
            _joined.SelectedIndex = _cfg.JoinedIndex;
            AddRow(grid, r++, "Cimiento unido a otros elementos:", _joined,
                   "Que geometria usar cuando columnas o losas le quitan hormigon a la viga (Unir geometria o recorte). Auto: la seccion se lee de la " +
                   "geometria completa de la familia (todo el canto aunque la losa lo tape) y la longitud del solido cortado (entre caras de columna). " +
                   "Un muro (sobrecimiento) no tiene geometria de familia: se usa siempre su solido cortado, y si las columnas unidas con prioridad " +
                   "lo parten en trozos, cada trozo se arma como un tramo aparte (sin armadura del muro dentro de la columna). " +
                   "Se aplica al volver a lanzar el comando (guardalo como valor por defecto).");
            _partition = new TextBox { Text = _cfg.PartitionTemplate, Margin = Pad };
            AddRow(grid, r++, "Particion:", _partition, "Plantilla del parametro Particion de cada barra. Comodines: " + PartitionName.Help);
            _partitionPreview = new TextBlock { Foreground = RevitTheme.Muted, Margin = Pad, TextWrapping = TextWrapping.Wrap };
            AddRow(grid, r++, "", _partitionPreview, null);
            group.Content = grid;
            return group;
        }

        private UIElement BuildPreviews()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.3, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var secGroup = new GroupBox { Header = "Seccion (rueda: zoom, arrastrar: mover, doble clic: encajar)", Padding = new Thickness(4) };
            var secPanel = new DockPanel();
            _previewCaption = new TextBlock { Foreground = RevitTheme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(_previewCaption, Dock.Top);
            secPanel.Children.Add(_previewCaption);
            var legend = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            LegendItem(legend, SectionPreview.CornerBrush, "barra extrema de capa");
            LegendItem(legend, SectionPreview.IntermediateBrush, "barra intermedia");
            LegendItem(legend, SectionPreview.SideBrush, "barra lateral");
            LegendItem(legend, SectionPreview.BastonBrush, "baston");
            LegendItem(legend, SectionPreview.StirrupBrush, "estribo (con sus ganchos)");
            DockPanel.SetDock(legend, Dock.Bottom);
            secPanel.Children.Add(legend);
            _preview = new SectionPreview { MinHeight = 200 };
            _preview.SelectionChanged += () => Refresh();
            secPanel.Children.Add(new Border { BorderBrush = RevitTheme.Border, BorderThickness = new Thickness(1), Child = _preview });
            secGroup.Content = secPanel;
            Grid.SetRow(secGroup, 0);
            grid.Children.Add(secGroup);

            var elvGroup = new GroupBox { Header = "Alzado desarrollado del recorrido: tramos, barras corridas, bastones y estribos", Padding = new Thickness(4), Margin = new Thickness(0, 6, 0, 0) };
            _elevation = new ElevationPreview { MinHeight = 150 };
            elvGroup.Content = new Border { BorderBrush = RevitTheme.Border, BorderThickness = new Thickness(1), Child = _elevation };
            Grid.SetRow(elvGroup, 1);
            grid.Children.Add(elvGroup);

            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.55, GridUnitType.Star) });
            var bGroup = new GroupBox { Header = "Alzado: solo los bastones", Padding = new Thickness(4), Margin = new Thickness(0, 6, 0, 0) };
            _bastonPreview = new BastonPreview { MinHeight = 90 };
            bGroup.Content = new Border { BorderBrush = RevitTheme.Border, BorderThickness = new Thickness(1), Child = _bastonPreview };
            Grid.SetRow(bGroup, 2);
            grid.Children.Add(bGroup);
            return grid;
        }

        private static void LegendItem(Panel panel, Brush brush, string text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            sp.Children.Add(new System.Windows.Shapes.Rectangle { Width = 12, Height = 12, Fill = brush, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(sp);
        }

        private UIElement BuildButtons()
        {
            var panel = new DockPanel();
            _message = new TextBlock { Foreground = RevitTheme.Error, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(buttons, Dock.Right);

            var save = new Button { Content = "Guardar como valores por defecto", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 0, 4, 0) };
            save.ToolTip = "Guarda lo elegido en config.json (" + AppConfig.ConfigPath() + ") para las proximas veces.";
            save.Click += (s, e) =>
            {
                AppConfig c = ReadConfig(out string err);
                if (err != null) { _message.Foreground = RevitTheme.Error; _message.Text = err; return; }
                try { c.Save(); _message.Foreground = RevitTheme.Ok; _message.Text = "Guardado en " + AppConfig.ConfigPath(); }
                catch (Exception ex) { _message.Foreground = RevitTheme.Error; _message.Text = "No se pudo guardar: " + ex.Message; }
            };
            buttons.Children.Add(save);

            _buildButton = new Button { Content = "Armar", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(4, 0, 4, 0), FontWeight = FontWeights.SemiBold, IsDefault = true };
            _buildButton.Click += (s, e) => OnBuild();
            buttons.Children.Add(_buildButton);

            var cancel = new Button { Content = "Cancelar", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 0, 0, 0), IsCancel = true };
            buttons.Children.Add(cancel);

            panel.Children.Add(buttons);
            panel.Children.Add(_message);
            return panel;
        }

        // ------------------------------------------------------------------
        // Controles auxiliares
        // ------------------------------------------------------------------
        private static Grid FormGrid()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return grid;
        }

        private static void Put(Grid grid, UIElement el, int r, int c)
        {
            Grid.SetRow(el, r); Grid.SetColumn(el, c);
            grid.Children.Add(el);
        }

        private void AddRow(Grid grid, int row, string label, FrameworkElement control, string tip)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lb = new TextBlock { Text = label, Margin = Pad, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lb, row); Grid.SetColumn(lb, 0);
            grid.Children.Add(lb);
            if (tip != null) { control.ToolTip = tip; lb.ToolTip = tip; }
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(control, row); Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            Hook(control);
        }

        private void Hook(FrameworkElement c)
        {
            if (c is TextBox tb) tb.TextChanged += (s, e) => Refresh();
            else if (c is ComboBox cb) cb.SelectionChanged += (s, e) => Refresh();
            else if (c is CheckBox ck) { ck.Checked += (s, e) => Refresh(); ck.Unchecked += (s, e) => Refresh(); }
        }

        private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static TextBox NumBox(double v) => new TextBox { Text = Num(v), Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Margin = Pad };

        private string TypeDisplay(string name) =>
            _diametersMm.TryGetValue(name, out double mm) ? name + " (" + Num(mm) + " mm)" : name;

        private ComboBox TypeCombo(string current)
        {
            var cb = new ComboBox { Margin = Pad };
            foreach (string n in _barTypes) cb.Items.Add(TypeDisplay(n));
            string match = RebarGenerator.MatchName(_barTypes, current);
            cb.SelectedIndex = match == null ? -1 : _barTypes.IndexOf(match);
            return cb;
        }

        private string TypeOf(ComboBox cb) =>
            cb.SelectedItem is string d && _typeByDisplay.TryGetValue(d, out string n) ? n : "";

        private ComboBox HookCombo(string current)
        {
            var cb = new ComboBox { Margin = Pad };
            cb.Items.Add(NoHook);
            foreach (string n in _hookTypes) cb.Items.Add(n);
            string match = RebarGenerator.MatchName(_hookTypes, current);
            cb.SelectedIndex = match == null ? 0 : _hookTypes.IndexOf(match) + 1;
            return cb;
        }

        private static string HookOf(ComboBox cb) => cb.SelectedIndex <= 0 ? "" : (string)cb.SelectedItem;

        /// <summary>Angulo en grados del tipo de gancho (0 = sin gancho).</summary>
        private double HookAngle(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            string match = RebarGenerator.MatchName(_hookTypes, name);
            if (match != null && _hookAngles.TryGetValue(match, out double deg) && deg > 0) return deg;
            return 135;
        }

        private static ComboBox OrientCombo(bool left)
        {
            var cb = new ComboBox { Margin = Pad, Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
            cb.Items.Add("Izquierda");
            cb.Items.Add("Derecha");
            cb.SelectedIndex = left ? 0 : 1;
            return cb;
        }

        private double DiameterFt(string typeName)
        {
            string match = RebarGenerator.MatchName(_barTypes, typeName);
            return match != null && _diametersMm.TryGetValue(match, out double mm) ? BeamSection.Mm(mm) : 0;
        }

        // ------------------------------------------------------------------
        // Lectura de la configuracion desde los controles
        // ------------------------------------------------------------------
        private AppConfig ReadConfig(out string error)
        {
            var errors = new List<string>();
            AppConfig c = _cfg.Clone();

            foreach (bool top in new[] { true, false })
            {
                ReadLayerRows(top, errors);
                c.Face(top).Layers = _layerStore[top].Select(l => l.Clone()).ToList();
            }
            c.Longitudinal.StartExtensionMm = ReadNum(_longStart, "prolongacion en el inicio", 0, errors);
            c.Longitudinal.EndExtensionMm = ReadNum(_longEnd, "prolongacion en el fin", 0, errors);
            c.Longitudinal.EndCoverMm = ReadNum(_endCover, "recubrimiento en extremos", 0, errors);
            c.Longitudinal.AnchorInAdjacent = _anchorAdjacent.IsChecked == true;
            c.Longitudinal.LegMm = ReadNum(_leg, "patilla", 0, errors);
            c.Longitudinal.LegAtStart = _legStart.IsChecked == true;
            c.Longitudinal.LegAtEnd = _legEnd.IsChecked == true;
            c.Longitudinal.LayerClearMm = ReadNum(_layerClear, "separacion entre capas", 0, errors);
            c.Longitudinal.MinClearMm = ReadNum(_minClear, "separacion libre minima", 0, errors);
            c.SideBars.BarTypeName = TypeOf(_sideType);
            if (int.TryParse(_sidePairs.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sp) && sp >= 0)
            { c.SideBars.Pairs = sp; _sidePairs.ClearValue(Control.BorderBrushProperty); }
            else { errors.Add("laterales: numero de pares no valido"); _sidePairs.BorderBrush = RevitTheme.Error; }

            ReadBastonRows(errors);
            c.Bastones = _bastonStore.Select(b => b.Clone()).ToList();

            c.Stirrups.BarTypeName = TypeOf(_stType);
            c.Stirrups.HookTypeName = HookOf(_stHook);
            c.Stirrups.HookOrientation = _stOrient.SelectedIndex == 1 ? "right" : "left";
            c.Stirrups.Distribution = _stDist.Text.Trim();
            if (StirrupLayout.Parse(c.Stirrups.Distribution, out string derr) == null) errors.Add("distribucion de estribos: " + derr);
            c.Stirrups.Symmetric = _stSym.IsChecked == true;
            c.Stirrups.StartOffsetMm = ReadNum(_stStartOff, "desfase en el inicio", 0, errors);
            c.Stirrups.EndOffsetMm = ReadNum(_stEndOff, "desfase en el fin", 0, errors);

            c.CoverMm = ReadNum(_cover, "recubrimiento", 0, errors);
            c.JoinedGeometry = AppConfig.JoinedModes[Math.Max(0, Math.Min(2, _joined.SelectedIndex))];
            c.PartitionTemplate = _partition.Text.Trim();
            c.Normalize();

            error = errors.Count == 0 ? null : string.Join(" | ", errors);
            return c;
        }

        private static double ReadNum(TextBox tb, string label, double min, List<string> errors)
        {
            if (!StirrupLayout.TryNumber(tb.Text, out double v) || v < min)
            {
                errors?.Add(label + ": numero no valido" + (min > 0 ? " (minimo " + Num(min) + ")" : ""));
                if (errors != null) tb.BorderBrush = RevitTheme.Error;
                return min;
            }
            tb.ClearValue(Control.BorderBrushProperty);
            return v;
        }

        /// <summary>Tipos de barra que faltan (obligatorios segun lo activado).</summary>
        private List<string> MissingTypes(AppConfig c)
        {
            var missing = new List<string>();
            foreach (bool top in new[] { true, false })
                for (int i = 0; i < c.Face(top).Layers.Count; i++)
                {
                    LayerCfg l = c.Face(top).Layers[i];
                    bool used = i == 0 || l.Count > 0 || _items.Any(it => it.Own(top, i + 1) > 0);
                    if (used && string.IsNullOrEmpty(l.BarTypeName)) missing.Add("capa " + (top ? "S" : "I") + (i + 1));
                }
            if (string.IsNullOrEmpty(c.Stirrups.BarTypeName)) missing.Add("estribos");
            if ((c.SideBars.Pairs > 0 || _items.Any(it => it.OwnSide() > 0)) && string.IsNullOrEmpty(c.SideBars.BarTypeName)) missing.Add("barras laterales");
            for (int i = 0; i < c.Bastones.Count; i++)
                if (string.IsNullOrEmpty(c.Bastones[i].BarTypeName)) missing.Add("baston " + (i + 1));
            return missing;
        }

        private void MarkTypes(AppConfig c)
        {
            var combos = new List<(ComboBox cb, bool required)> { (_stType, true) };
            foreach (bool top in new[] { true, false })
                for (int i = 0; i < _layerRows[top].Count; i++)
                {
                    LayerCfg l = _layerRows[top][i].Cfg;
                    bool used = i == 0 || l.Count > 0 || _items.Any(it => it.Own(top, i + 1) > 0);
                    combos.Add((_layerRows[top][i].Corner, used));
                }
            foreach (BastonRow row in _bastonRows) combos.Add((row.Type, true));
            combos.Add((_sideType, c.SideBars.Pairs > 0 || _items.Any(it => it.OwnSide() > 0)));
            foreach ((ComboBox cb, bool required) in combos)
            {
                if (_strictTypes && required && cb.SelectedIndex < 0) { cb.BorderBrush = RevitTheme.Error; cb.BorderThickness = new Thickness(2); }
                else { cb.ClearValue(Control.BorderBrushProperty); cb.ClearValue(Control.BorderThicknessProperty); }
            }
        }

        // ------------------------------------------------------------------
        // Actualizacion
        // ------------------------------------------------------------------
        private void Refresh()
        {
            if (_building) return;
            AppConfig scratch = ReadConfig(out string error);
            MarkTypes(scratch);

            double ds = DiameterFt(scratch.Stirrups.BarTypeName);
            // sin tipo elegido, se dibuja con un diametro orientativo para poder ver el esquema
            double dsDraw = ds > 0 ? ds : BeamSection.Mm(9.5), dbFallback = BeamSection.Mm(16);
            bool anyMissing = MissingTypes(scratch).Count > 0;

            // estado de cada viga con esta configuracion
            int ok = 0;
            foreach (HostAnalysis item in _items)
            {
                bool good = ItemStatus(item, scratch, dsDraw, dbFallback, out string text, out _, out _, out _);
                if (good) ok++;
                if (_itemRuns.TryGetValue(item, out var runs))
                {
                    runs.kind.Text = item.Kind + ": ";
                    runs.kind.Foreground = good ? RevitTheme.Ok : RevitTheme.Error;
                    runs.detail.Text = text;
                }
            }
            foreach (var kv in _distHints)
            {
                kv.Value.Text = "general: " + scratch.Stirrups.Distribution;
                kv.Value.Visibility = string.IsNullOrEmpty(kv.Key.DistributionOverride) ? Visibility.Visible : Visibility.Hidden;
            }
            _buildButton.Content = "Armar " + ok + " elemento(s)";
            _buildButton.IsEnabled = ok > 0 && error == null;

            // esquema del elemento seleccionado
            if (_selected != null && _selected.CanBuild)
            {
                ItemStatus(_selected, scratch, dsDraw, dbFallback, out string text, out BeamPlan plan, out List<StirrupRun> runs, out List<BastonRange> bastones);
                _previewCaption.Text = _selected.Tag + _selected.Section.Describe() + (anyMissing ? "  (sin tipo de barra elegido en algo: diametros orientativos)" : "");
                double hookDeg = HookAngle(scratch.Stirrups.HookTypeName);
                RefreshSelection(plan);
                foreach (bool top in new[] { true, false })
                    _layerSummary[top].Text = plan == null || plan.Error != null ? "" :
                        string.Join("   ", plan.LayersOf(top).Where(l => l.Main > 0).Select(l => l.Name + ": " + BeamPlan.DescribeLayer(l, false)));
                if (plan != null) _preview.Show(_selected.Section, plan, hookDeg); else _preview.Clear(text);
                EnsureAdjacent(_selected.Section);
                if (runs != null) _elevation.Show(_selected.Section, plan, runs, bastones, scratch); else _elevation.Clear(text);
                _bastonPreview.Show(_selected.Section, plan, bastones, scratch);
                _partitionPreview.Text = "Ejemplo: " + _selected.Partition(scratch, "estribo", "estribo");
                var bmsgs = new List<string>();
                if (bastones == null) RebarGenerator.BastonRanges(_selected.Section, scratch, out bmsgs);
                if (plan != null) bmsgs.AddRange(plan.Warnings.Where(w => w.StartsWith("baston", StringComparison.OrdinalIgnoreCase)));
                _bastonMessage.Text = string.Join(Environment.NewLine, bmsgs);
            }
            else
            {
                RefreshSelection(null);
                foreach (bool top in new[] { true, false }) _layerSummary[top].Text = "";
                _previewCaption.Text = "";
                _preview.Clear("Sin elemento armable");
                _elevation.Clear("");
                _bastonPreview.Clear();
                _partitionPreview.Text = "";
                _bastonMessage.Text = "";
            }

            if (error != null) { _message.Foreground = RevitTheme.Error; _message.Text = error; }
            else if (_message.Foreground == RevitTheme.Error) _message.Text = "";
        }

        /// <summary>Estado de una viga con la configuracion dada: true si se puede armar, y el texto para su fila.</summary>
        /// <summary>
        /// Carga una vez el hormigon contiguo del recorrido (cimientos a los que llega en T o en
        /// esquina, columnas) para que los esquemas dibujen el anclaje de los extremos tal y
        /// como lo va a crear el generador. Solo lectura del modelo.
        /// </summary>
        private void EnsureAdjacent(BeamSection s)
        {
            if (s == null || s.AdjacentLoaded) return;
            try { RebarGenerator.LoadAdjacent(_doc, s); }
            catch { s.AdjacentLoaded = true; }
        }

        private bool ItemStatus(HostAnalysis item, AppConfig cfg, double ds, double dbFallback,
                                out string text, out BeamPlan plan, out List<StirrupRun> runs, out List<BastonRange> bastones)
        {
            plan = null; runs = null; bastones = null;
            if (!item.CanBuild) { text = item.Error; return false; }
            try
            {
                plan = RebarGenerator.PlanFor(item, cfg, DiameterFt, ds, dbFallback);
                if (plan.Error != null) { text = item.Section.Describe() + " -> SIN ARMAR: " + plan.Error; return false; }
                runs = RebarGenerator.RunsFor(item, cfg, out string warn);
                bastones = RebarGenerator.BastonRanges(item.Section, cfg, out List<string> berr);
                int n = runs.Sum(r => r.Count);
                text = item.Section.Describe() + "; " + plan.Describe() + " (" + plan.DescribeLayers() + "); " + n + " estribos" +
                       (warn != null ? " (" + warn + ")" : "");
                if (berr.Count > 0) { text += " -> SIN ARMAR: " + string.Join(" | ", berr); return false; }
                return n > 0;
            }
            catch (Exception ex)
            {
                text = item.Section.Describe() + " -> SIN ARMAR: " + ex.Message;
                return false;
            }
        }

        private void OnBuild()
        {
            AppConfig c = ReadConfig(out string error);
            if (error != null) { _message.Foreground = RevitTheme.Error; _message.Text = error; return; }
            List<string> missing = MissingTypes(c);
            if (missing.Count > 0)
            {
                _strictTypes = true;
                MarkTypes(c);
                _message.Foreground = RevitTheme.Error;
                _message.Text = "Elige el tipo de barra de: " + string.Join(", ", missing) + ".";
                return;
            }
            Result = c;
            DialogResult = true;
            Close();
        }
    }
}
