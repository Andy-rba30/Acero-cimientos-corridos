using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arba.Comun;
using Autodesk.Revit.UI;

namespace StripFootingRebar
{
    /// <summary>
    /// Entrada de la aplicacion de cinta en Revit. Anade el boton "Cimientos/Sobrecimientos" al
    /// desplegable "Acero" del panel "Acero" de la pestana "ARBA". La pestana, los paneles y el
    /// desplegable los gestiona ArbaRibbon (ARBA-comun), compartido con los demas add-ins ARBA:
    /// el orden es el mismo sin importar que add-in cargue primero.
    /// </summary>
    public class RibbonApp : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                ArbaRibbon.Ensure(app);

                string assembly = Assembly.GetExecutingAssembly().Location;
                var data = new PushButtonData("ARBA_Acero_Cimientos", "Cimientos/\nSobrecimientos", assembly, typeof(ArmarCimientoCommand).FullName)
                {
                    ToolTip = "Genera el armado de cimientos corridos y sobrecimientos (cimentaciones, vigas de cimentacion, suelos o muros) siguiendo su recorrido (esquinas incluidas): barras corridas por capas, bastones y estribos",
                    LongDescription = "Selecciona todos los tramos del cimiento corrido o sobrecimiento (tambien muros estructurales) y pulsa el boton. Los tramos que se tocan " +
                                      "por los extremos se encadenan en un recorrido: las barras corridas doblan en las esquinas y " +
                                      "los estribos van tramo a tramo. La ventana permite elegir capas, bastones, estribos y ganchos " +
                                      "con un esquema de la seccion y del alzado desarrollado. Las barras se marcan segun el contrato " +
                                      "ARBA " + ArbaContract.Version + " (particion \"CATEGORIA - CCO-marca\", ARBA - Origen = " +
                                      ArbaContract.CimientosCorridos.Origin + ").",
                    LargeImage = IconCimientos(32),
                    Image = IconCimientos(16)
                };

                ArbaRibbon.AddAcero(app, data);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("ARBA", "No se pudo anadir el boton Cimientos/Sobrecimientos a la cinta: " + ex.Message +
                                "\nEl comando sigue disponible en Complementos > Herramientas externas.");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;

        /// <summary>
        /// Icono del boton Cimientos/Sobrecimientos: seccion de un cimiento corrido apoyado en el
        /// terreno, con su estribo y dos capas de barras corridas (superior e inferior). Misma paleta
        /// que los iconos de los demas add-ins ARBA (hormigon gris, estribo verde azulado, barras granate).
        /// </summary>
        public static BitmapSource IconCimientos(int size)
        {
            double s = size / 32.0;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                var concrete = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9));
                var edge = new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 1.2 * s);
                var ground = new SolidColorBrush(Color.FromRgb(0xC9, 0xB3, 0x8A));
                var groundLine = new Pen(new SolidColorBrush(Color.FromRgb(0x8A, 0x6E, 0x45)), 1.0 * s);
                var stirrup = new Pen(new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0x7A)), 1.6 * s) { LineJoin = PenLineJoin.Round };
                var bar = new SolidColorBrush(Color.FromRgb(0x8B, 0x2E, 0x2E));

                // terreno: banda inferior con el cimiento enterrado hasta su fondo
                dc.DrawRectangle(ground, null, new System.Windows.Rect(0, 24 * s, 32 * s, 8 * s));
                dc.DrawLine(groundLine, new Point(0, 24 * s), new Point(2 * s, 24 * s));
                dc.DrawLine(groundLine, new Point(30 * s, 24 * s), new Point(32 * s, 24 * s));

                // cimiento corrido (seccion ancha) con el fondo en el terreno
                dc.DrawRectangle(concrete, edge, new System.Windows.Rect(2 * s, 8 * s, 28 * s, 22 * s));

                // estribo rectangular y dos capas de barras corridas
                dc.DrawRectangle(null, stirrup, new System.Windows.Rect(6 * s, 12 * s, 20 * s, 14 * s));
                double rr = 1.8 * s;
                foreach (Point p in new[]
                {
                    new Point(6 * s, 12 * s), new Point(16 * s, 12 * s), new Point(26 * s, 12 * s),
                    new Point(6 * s, 26 * s), new Point(16 * s, 26 * s), new Point(26 * s, 26 * s)
                })
                    dc.DrawEllipse(bar, null, p, rr, rr);
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }
}
