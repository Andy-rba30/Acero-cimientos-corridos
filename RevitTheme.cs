using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace FootingRebar
{
    /// <summary>
    /// Tema oscuro al estilo de Revit 2027 para las ventanas del add-in. Solo estilo:
    /// colores, bordes y plantillas de los controles (cajas de texto, desplegables,
    /// casillas, botones, grupos, barras de desplazamiento, tablas y tooltips) y la
    /// barra de titulo oscura de Windows. No cambia ningun comportamiento. Se aplica
    /// con RevitTheme.Apply(ventana) en el constructor, antes de construir el contenido.
    /// Los estilos estan en XAML dentro del codigo (XamlReader), asi el proyecto sigue
    /// sin depender del compilador de XAML. Si algo fallara al cargar el tema, la ventana
    /// se muestra con el aspecto normal de Windows.
    /// </summary>
    public static class RevitTheme
    {
        // --- paleta (la misma que usa la interfaz oscura de Revit) ---
        public static readonly Brush Window = Make("#2B2B2B");      // fondo de la ventana
        public static readonly Brush Panel = Make("#313131");       // fondo de los grupos
        public static readonly Brush Header = Make("#3A3A3A");      // cabecera de grupo / tabla
        public static readonly Brush Input = Make("#3C3C3C");       // fondo de cajas y desplegables
        public static readonly Brush Border = Make("#555555");      // bordes
        public static readonly Brush Text = Make("#E6E6E6");        // texto normal
        public static readonly Brush Muted = Make("#A8A8A8");       // texto secundario (antes DimGray)
        public static readonly Brush Hint = Make("#8C8C8C");        // marcas de agua (antes Gray)
        public static readonly Brush Accent = Make("#2F7BD9");      // azul de Revit (seleccion, boton principal)
        public static readonly Brush Selection = Make("#1F3F66");   // fila seleccionada
        public static readonly Brush Ok = Make("#6FCF7F");          // texto de exito (antes DarkGreen)
        public static readonly Brush Error = Make("#FF6B6B");       // texto de error (antes Firebrick)
        public static readonly Brush OwnValue = Make("#4A4420");    // casilla con valor propio (antes LightYellow)
        public static readonly Brush Invalid = Make("#5A2E2E");     // casilla con valor no valido (antes MistyRose)
        public static readonly Brush Paper = Make("#FFFFFF");       // fondo de los esquemas (como el area de dibujo)

        private static Brush Make(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private static ResourceDictionary _dictionary;

        /// <summary>Diccionario de estilos del tema (se carga una vez).</summary>
        public static ResourceDictionary Dictionary
        {
            get
            {
                if (_dictionary == null) _dictionary = (ResourceDictionary)XamlReader.Parse(Xaml);
                return _dictionary;
            }
        }

        /// <summary>Aplica el tema a la ventana: fondo, texto, estilos de los controles y barra de titulo oscura.</summary>
        public static void Apply(System.Windows.Window window)
        {
            if (window == null) return;
            try
            {
                window.Resources.MergedDictionaries.Add(Dictionary);
                window.Background = Window;
                window.Foreground = Text;
                window.UseLayoutRounding = true;
                window.SnapsToDevicePixels = true;
                TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
            }
            catch (Exception)
            {
                // sin tema: la ventana funciona igual con el aspecto normal
            }
            window.SourceInitialized += (s, e) => DarkTitleBar(window);
        }

        // --- barra de titulo oscura (Windows 10 1809+ / Windows 11) ---
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private static void DarkTitleBar(System.Windows.Window window)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                int dark = 1;
                // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 en Windows 10 20H1+, 19 en versiones anteriores
                if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
            }
            catch (Exception) { }
        }

        // --- estilos ---
        private const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>

  <SolidColorBrush x:Key='T.Window' Color='#2B2B2B'/>
  <SolidColorBrush x:Key='T.Panel' Color='#313131'/>
  <SolidColorBrush x:Key='T.Header' Color='#3A3A3A'/>
  <SolidColorBrush x:Key='T.Input' Color='#3C3C3C'/>
  <SolidColorBrush x:Key='T.InputDisabled' Color='#333333'/>
  <SolidColorBrush x:Key='T.Border' Color='#555555'/>
  <SolidColorBrush x:Key='T.BorderHover' Color='#8A8A8A'/>
  <SolidColorBrush x:Key='T.Text' Color='#E6E6E6'/>
  <SolidColorBrush x:Key='T.TextDisabled' Color='#7A7A7A'/>
  <SolidColorBrush x:Key='T.Accent' Color='#2F7BD9'/>
  <SolidColorBrush x:Key='T.AccentHover' Color='#3D8BE8'/>
  <SolidColorBrush x:Key='T.AccentPressed' Color='#256AC0'/>
  <SolidColorBrush x:Key='T.Selection' Color='#1F3F66'/>
  <SolidColorBrush x:Key='T.Button' Color='#3E3E42'/>
  <SolidColorBrush x:Key='T.ButtonHover' Color='#4C4C52'/>
  <SolidColorBrush x:Key='T.ButtonPressed' Color='#2F7BD9'/>
  <SolidColorBrush x:Key='T.Thumb' Color='#6A6A6A'/>
  <SolidColorBrush x:Key='T.ThumbHover' Color='#8A8A8A'/>
  <SolidColorBrush x:Key='T.Row' Color='#2F2F2F'/>
  <SolidColorBrush x:Key='T.RowAlt' Color='#353535'/>
  <SolidColorBrush x:Key='T.GridLine' Color='#454545'/>

  <!-- texto -->
  <Style TargetType='TextBlock'>
    <Setter Property='Foreground' Value='{StaticResource T.Text}'/>
  </Style>
  <Style TargetType='Label'>
    <Setter Property='Foreground' Value='{StaticResource T.Text}'/>
  </Style>

  <!-- tooltip -->
  <Style TargetType='ToolTip'>
    <Setter Property='Background' Value='{StaticResource T.Header}'/>
    <Setter Property='Foreground' Value='{StaticResource T.Text}'/>
    <Setter Property='BorderBrush' Value='{StaticResource T.Border}'/>
    <Setter Property='Padding' Value='8,5'/>
    <Setter Property='MaxWidth' Value='520'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ToolTip'>
          <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='3' Padding='{TemplateBinding Padding}'>
            <ContentPresenter TextElement.Foreground='{TemplateBinding Foreground}'>
              <ContentPresenter.Resources>
                <Style TargetType='TextBlock'>
                  <Setter Property='TextWrapping' Value='Wrap'/>
                  <Setter Property='Foreground' Value='{StaticResource T.Text}'/>
                </Style>
              </ContentPresenter.Resources>
            </ContentPresenter>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- grupo -->
  <Style TargetType='GroupBox'>
    <Setter Property='Foreground' Value='{StaticResource T.Text}'/>
    <Setter Property='BorderBrush' Value='{StaticResource T.Border}'/>
    <Setter Property='Background' Value='{StaticResource T.Panel}'/>
    <Setter Property='Margin' Value='0,0,0,6'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='GroupBox'>
          <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='3' SnapsToDevicePixels='True'>
            <DockPanel>
              <Border DockPanel.Dock='Top' Background='{StaticResource T.Header}' Padding='8,4' CornerRadius='2,2,0,0'>
                <ContentPresenter ContentSource='Header' RecognizesAccessKey='True' TextElement.Foreground='{StaticResource T.Text}' TextElement.FontWeight='SemiBold'/>
              </Border>
              <ContentPresenter Margin='{TemplateBinding Padding}'/>
            </DockPanel>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- caja de texto -->
  <Style TargetType='TextBox'>
    <Setter Property='Background' Value='{StaticResource T.Input}'/>
    <Setter Property='Foreground' Value='{StaticResource T.Text}'/>
    <Setter Property='BorderBrush' Value='{StaticResource T.Border}'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='CaretBrush' Value='{StaticResource T.Text}'/>
    <Setter Property='SelectionBrush' Value='{StaticResource T.Accent}'/>
    <Setter Property='Padding' Value='4,2'/>
    <Setter Property='MinHeight' Value='24'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TextBox'>
          <Border x:Name='bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='2' SnapsToDevicePixels='True'>
            <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}' VerticalAlignment='Center' Background='Transparent'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource T.BorderHover}'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource T.Accent}'/>
            </Trigger>
            <Trigger Property='IsReadOnly' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource T.InputDisabled}'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Foreground' Value='{StaticResource T.TextDisabled}'/>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource T.InputDisabled}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- desplegable -->
  <Style TargetType='ComboBoxItem'>
    <Setter Property='Foreground' Value='{StaticResource T.Text}'/>
    <Setter Property='Padding' Value='8,4'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBoxItem'>
          <Border x:Name='bd' Background='Transparent' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
            <ContentPresenter TextElement.Foreground='{TemplateBinding Foreground}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsHighlighted' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource T.Accent}'/>
            </Trigger>
            <Trigger Property='IsSelected' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource T.Selection}'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Foreground' Value='{StaticResource T.TextDisabled}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='ComboBox'>
    <Setter Property='Background' Value='{StaticResource T.Input}'/>
    <Setter Property='Foreground' Value='{StaticResource T.Text}'/>
    <Setter Property='BorderBrush' Value='{StaticResource T.Border}'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='6,3'/>
    <Setter Property='MinHeight' Value='24'/>
    <Setter Property='ScrollViewer.CanContentScroll' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBox'>
          <Grid>
            <ToggleButton x:Name='toggle' Focusable='False' ClickMode='Press'
                          IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
              <ToggleButton.Template>
                <ControlTemplate TargetType='ToggleButton'>
                  <Border x:Name='bd' Background='{Binding Background, RelativeSource={RelativeSource AncestorType=ComboBox}}'
                          BorderBrush='{Binding BorderBrush, RelativeSource={RelativeSource AncestorType=ComboBox}}'
                          BorderThickness='{Binding BorderThickness, RelativeSource={RelativeSource AncestorType=ComboBox}}'
                          CornerRadius='2' SnapsToDevicePixels='True'>
                    <Path HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,8,0' Data='M0,0 L4,4 L8,0 Z' Fill='#CCCCCC'/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property='IsMouseOver' Value='True'>
                      <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource T.BorderHover}'/>
                    </Trigger>
                    <Trigger Property='IsChecked' Value='True'>
                      <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource T.Accent}'/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
                              ContentTemplateSelector='{TemplateBinding ItemTemplateSelector}'
                              Margin='{TemplateBinding Padding}' HorizontalAlignment='Left' VerticalAlignment='Center' IsHitTestVisible='False'
                              TextElement.Foreground='{TemplateBinding Foreground}'/>
            <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>
              <Border Background='{StaticResource T.Header}' BorderBrush='{StaticResource T.Border}' BorderThickness='1' CornerRadius='2'
                      MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'
                      MaxHeight='{Binding MaxDropDownHeight, RelativeSource={RelativeSource TemplatedParent}}'>
                <ScrollViewer SnapsToDevicePixels='True'>
                  <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Foreground' Value='{StaticResource T.TextDisabled}'/>
              <Setter Property='Background' Value='{StaticResource T.InputDisabled}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- casilla -->
  <Style TargetType='CheckBox'>
    <Setter Property='Foreground' Value='{StaticResource T.Text}'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='CheckBox'>
          <StackPanel Orientation='Horizontal' Background='Transparent'>
            <Border x:Name='box' Width='15' Height='15' Background='{StaticResource T.Input}' BorderBrush='{StaticResource T.BorderHover}' BorderThickness='1' CornerRadius='2' VerticalAlignment='Center' SnapsToDevicePixels='True'>
              <Path x:Name='mark' Data='M2,7 L6,11 L13,3' Stroke='White' StrokeThickness='2' Visibility='Collapsed' SnapsToDevicePixels='False'/>
            </Border>
            <ContentPresenter Margin='6,0,0,0' VerticalAlignment='Center' RecognizesAccessKey='True' TextElement.Foreground='{TemplateBinding Foreground}'/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='box' Property='Background' Value='{StaticResource T.Accent}'/>
              <Setter TargetName='box' Property='BorderBrush' Value='{StaticResource T.Accent}'/>
              <Setter TargetName='mark' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='box' Property='BorderBrush' Value='{StaticResource T.AccentHover}'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Foreground' Value='{StaticResource T.TextDisabled}'/>
              <Setter TargetName='box' Property='Opacity' Value='0.5'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- boton de opcion -->
  <Style TargetType='RadioButton'>
    <Setter Property='Foreground' Value='{StaticResource T.Text}'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RadioButton'>
          <StackPanel Orientation='Horizontal' Background='Transparent'>
            <Grid Width='15' Height='15' VerticalAlignment='Center'>
              <Ellipse x:Name='ring' Fill='{StaticResource T.Input}' Stroke='{StaticResource T.BorderHover}' StrokeThickness='1'/>
              <Ellipse x:Name='dot' Fill='White' Margin='4' Visibility='Collapsed'/>
            </Grid>
            <ContentPresenter Margin='6,0,0,0' VerticalAlignment='Center' RecognizesAccessKey='True' TextElement.Foreground='{TemplateBinding Foreground}'/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='ring' Property='Fill' Value='{StaticResource T.Accent}'/>
              <Setter TargetName='ring' Property='Stroke' Value='{StaticResource T.Accent}'/>
              <Setter TargetName='dot' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='ring' Property='Stroke' Value='{StaticResource T.AccentHover}'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Foreground' Value='{StaticResource T.TextDisabled}'/>
              <Setter TargetName='ring' Property='Opacity' Value='0.5'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- boton (el boton por defecto de la ventana, en azul) -->
  <Style TargetType='Button'>
    <Setter Property='Background' Value='{StaticResource T.Button}'/>
    <Setter Property='Foreground' Value='{StaticResource T.Text}'/>
    <Setter Property='BorderBrush' Value='{StaticResource T.Border}'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='10,4'/>
    <Setter Property='MinHeight' Value='26'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='3' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='True' TextElement.Foreground='{TemplateBinding Foreground}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsDefault' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource T.Accent}'/>
              <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource T.Accent}'/>
            </Trigger>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource T.ButtonHover}'/>
              <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource T.BorderHover}'/>
            </Trigger>
            <MultiTrigger>
              <MultiTrigger.Conditions>
                <Condition Property='IsDefault' Value='True'/>
                <Condition Property='IsMouseOver' Value='True'/>
              </MultiTrigger.Conditions>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource T.AccentHover}'/>
              <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource T.AccentHover}'/>
            </MultiTrigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource T.AccentPressed}'/>
              <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource T.AccentPressed}'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Foreground' Value='{StaticResource T.TextDisabled}'/>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource T.InputDisabled}'/>
              <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource T.Border}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- barras de desplazamiento -->
  <Style x:Key='T.ScrollThumb' TargetType='Thumb'>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Thumb'>
          <Border x:Name='bd' Background='{StaticResource T.Thumb}' CornerRadius='4' Margin='2'/>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='{StaticResource T.ThumbHover}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='T.ScrollPage' TargetType='RepeatButton'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RepeatButton'>
          <Rectangle Fill='Transparent'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='ScrollBar'>
    <Setter Property='Background' Value='{StaticResource T.Window}'/>
    <Setter Property='Width' Value='12'/>
    <Setter Property='MinWidth' Value='12'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Grid Background='{TemplateBinding Background}'>
            <Track x:Name='PART_Track' IsDirectionReversed='True'>
              <Track.DecreaseRepeatButton>
                <RepeatButton Style='{StaticResource T.ScrollPage}' Command='ScrollBar.PageUpCommand'/>
              </Track.DecreaseRepeatButton>
              <Track.IncreaseRepeatButton>
                <RepeatButton Style='{StaticResource T.ScrollPage}' Command='ScrollBar.PageDownCommand'/>
              </Track.IncreaseRepeatButton>
              <Track.Thumb>
                <Thumb Style='{StaticResource T.ScrollThumb}'/>
              </Track.Thumb>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property='Orientation' Value='Horizontal'>
        <Setter Property='Width' Value='Auto'/>
        <Setter Property='MinWidth' Value='0'/>
        <Setter Property='Height' Value='12'/>
        <Setter Property='MinHeight' Value='12'/>
        <Setter Property='Template'>
          <Setter.Value>
            <ControlTemplate TargetType='ScrollBar'>
              <Grid Background='{TemplateBinding Background}'>
                <Track x:Name='PART_Track' IsDirectionReversed='False'>
                  <Track.DecreaseRepeatButton>
                    <RepeatButton Style='{StaticResource T.ScrollPage}' Command='ScrollBar.PageLeftCommand'/>
                  </Track.DecreaseRepeatButton>
                  <Track.IncreaseRepeatButton>
                    <RepeatButton Style='{StaticResource T.ScrollPage}' Command='ScrollBar.PageRightCommand'/>
                  </Track.IncreaseRepeatButton>
                  <Track.Thumb>
                    <Thumb Style='{StaticResource T.ScrollThumb}'/>
                  </Track.Thumb>
                </Track>
              </Grid>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Trigger>
    </Style.Triggers>
  </Style>

  <!-- tablas -->
  <Style TargetType='DataGridColumnHeader'>
    <Setter Property='Background' Value='{StaticResource T.Header}'/>
    <Setter Property='Foreground' Value='{StaticResource T.Text}'/>
    <Setter Property='BorderBrush' Value='{StaticResource T.GridLine}'/>
    <Setter Property='BorderThickness' Value='0,0,1,1'/>
    <Setter Property='Padding' Value='6,4'/>
    <Setter Property='FontWeight' Value='SemiBold'/>
  </Style>
  <Style TargetType='DataGridRow'>
    <Setter Property='Background' Value='{StaticResource T.Row}'/>
    <Setter Property='Foreground' Value='{StaticResource T.Text}'/>
    <Style.Triggers>
      <Trigger Property='IsSelected' Value='True'>
        <Setter Property='Background' Value='{StaticResource T.Selection}'/>
      </Trigger>
      <Trigger Property='IsMouseOver' Value='True'>
        <Setter Property='Background' Value='{StaticResource T.RowAlt}'/>
      </Trigger>
    </Style.Triggers>
  </Style>
  <Style TargetType='DataGridCell'>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='BorderThickness' Value='0'/>
    <Setter Property='Padding' Value='4,2'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='DataGridCell'>
          <Border Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
            <ContentPresenter VerticalAlignment='Center'/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property='IsSelected' Value='True'>
        <Setter Property='Background' Value='{StaticResource T.Selection}'/>
        <Setter Property='Foreground' Value='{StaticResource T.Text}'/>
      </Trigger>
    </Style.Triggers>
  </Style>
  <Style TargetType='DataGrid'>
    <Setter Property='Background' Value='{StaticResource T.Window}'/>
    <Setter Property='Foreground' Value='{StaticResource T.Text}'/>
    <Setter Property='BorderBrush' Value='{StaticResource T.Border}'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='RowBackground' Value='{StaticResource T.Row}'/>
    <Setter Property='AlternatingRowBackground' Value='{StaticResource T.RowAlt}'/>
    <Setter Property='HorizontalGridLinesBrush' Value='{StaticResource T.GridLine}'/>
    <Setter Property='VerticalGridLinesBrush' Value='{StaticResource T.GridLine}'/>
  </Style>

  <!-- separador y barra de progreso -->
  <Style TargetType='Separator'>
    <Setter Property='Background' Value='{StaticResource T.Border}'/>
  </Style>
  <Style TargetType='ProgressBar'>
    <Setter Property='Background' Value='{StaticResource T.Input}'/>
    <Setter Property='Foreground' Value='{StaticResource T.Accent}'/>
    <Setter Property='BorderBrush' Value='{StaticResource T.Border}'/>
  </Style>
</ResourceDictionary>";
    }
}
