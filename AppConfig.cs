using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FootingRebar
{
    /// <summary>
    /// Una capa de barras longitudinales de una cara (superior o inferior). La capa 1 es
    /// la pegada al estribo; las siguientes se apilan hacia el interior con la separacion
    /// libre entre capas. Sus dos barras extremas (en la capa 1, las esquinas del estribo)
    /// llevan un tipo de barra y las intermedias otro, asi una capa puede ser "2 de 3/4 +
    /// 1 de 5/8".
    /// </summary>
    public class LayerCfg
    {
        /// <summary>Tipo de barra (RebarBarType) de las dos barras extremas de la capa: exacto, o un fragmento que lo identifique.</summary>
        public string BarTypeName { get; set; } = "";
        /// <summary>Tipo de barra de las intermedias de la capa. Vacio = el mismo que las extremas.</summary>
        public string IntermediateBarTypeName { get; set; } = "";
        /// <summary>Total de barras de la capa (extremas incluidas). Minimo 2 en la capa 1.</summary>
        public int Count { get; set; } = 2;

        public LayerCfg Clone() => (LayerCfg)MemberwiseClone();

        [JsonIgnore]
        public string IntermediateOrCorner => string.IsNullOrWhiteSpace(IntermediateBarTypeName) ? BarTypeName : IntermediateBarTypeName;
    }

    /// <summary>Armado corrido de una cara de la viga (superior o inferior): sus capas.</summary>
    public class FaceCfg
    {
        public List<LayerCfg> Layers { get; set; } = new List<LayerCfg> { new LayerCfg() };

        public FaceCfg Clone()
        {
            var c = new FaceCfg { Layers = new List<LayerCfg>() };
            foreach (LayerCfg l in Layers) c.Layers.Add(l.Clone());
            return c;
        }
    }

    /// <summary>Barras laterales (capa intermedia): pares simetricos pegados a las ramas del estribo, a alturas iguales entre las capas superiores e inferiores.</summary>
    public class SideBarsCfg
    {
        public string BarTypeName { get; set; } = "";
        /// <summary>Numero de pares (cada par = una barra en cada costado, a la misma altura). 0 = sin laterales.</summary>
        public int Pairs { get; set; } = 0;
    }

    /// <summary>Reglas comunes de las barras longitudinales corridas.</summary>
    public class LongitudinalCfg
    {
        /// <summary>Prolongacion de las barras corridas mas alla de la cara de inicio de la viga (mm, anclaje en el apoyo). 0 = terminan en el recubrimiento del extremo.</summary>
        public double StartExtensionMm { get; set; } = 0;
        /// <summary>Idem en la cara final.</summary>
        public double EndExtensionMm { get; set; } = 0;
        /// <summary>Recubrimiento en los extremos de la viga cuando la barra no se prolonga (mm).</summary>
        public double EndCoverMm { get; set; } = 40;
        /// <summary>
        /// En un extremo sin prolongacion que llega a otro cimiento (en T, en una esquina que no
        /// sigue el recorrido o en la que cierra un anillo), las barras corridas y los bastones
        /// de ese extremo atraviesan el hormigon contiguo y terminan en su cara opuesta, menos
        /// el recubrimiento de extremos, en vez de quedarse en la cara de este cimiento.
        /// </summary>
        public bool AnchorInAdjacent { get; set; } = true;
        /// <summary>Patilla a 90 grados en los extremos prolongados (mm): las superiores doblan hacia abajo y las inferiores hacia arriba. 0 = sin patilla. Necesita prolongacion.</summary>
        public double LegMm { get; set; } = 0;
        public bool LegAtStart { get; set; } = true;
        public bool LegAtEnd { get; set; } = true;
        /// <summary>Separacion libre entre capas de una misma cara (mm).</summary>
        public double LayerClearMm { get; set; } = 25;
        /// <summary>Separacion libre minima entre barras de una misma capa (mm); ademas nunca menor que un diametro.</summary>
        public double MinClearMm { get; set; } = 25;
    }

    /// <summary>
    /// Baston: barra corta de refuerzo en la cara superior o inferior, en los apoyos
    /// (inicio, fin o ambos), en el centro del vano o en un tramo cualquiera. Se coloca en
    /// una capa (la que se indique, o la primera en la que quepa) en los huecos entre las
    /// barras corridas de esa capa, o en una capa nueva por dentro si no cabe.
    /// </summary>
    public class BastonCfg
    {
        /// <summary>"top" (superior) o "bottom" (inferior).</summary>
        public string Face { get; set; } = "top";
        /// <summary>"start" (inicio), "end" (fin), "both" (ambos extremos), "center" (centro del vano) o "custom" (tramo desde/hasta).</summary>
        public string Position { get; set; } = "both";
        public string BarTypeName { get; set; } = "";
        /// <summary>Numero de barras del baston en la seccion.</summary>
        public int Count { get; set; } = 1;
        /// <summary>
        /// true = apilado por dentro de las barras corridas de su cara (tangente a la capa mas
        /// interior, con GapMm de hueco), como los bastones del add-in de muros; false =
        /// intercalado en la capa 1, en los huecos entre las barras corridas.
        /// </summary>
        public bool Stacked { get; set; } = true;
        /// <summary>Solo apilados: hueco entre el baston y las barras corridas (mm). 0 = tocandolas.</summary>
        public double GapMm { get; set; } = 0;
        /// <summary>
        /// Longitud del baston en mm ("1500"; en metros si es menor de 5). En inicio/fin/ambos
        /// se mide desde la cara del apoyo hacia el vano; en centro es la longitud total, centrada.
        /// </summary>
        public string Length { get; set; } = "1500";
        /// <summary>Inicio/fin/ambos: prolongacion dentro del apoyo, mas alla de la cara de la viga (mm). 0 = empieza en el recubrimiento del extremo.</summary>
        public double AnchorMm { get; set; } = 0;
        /// <summary>Solo centro: longitudes distintas hacia el inicio y hacia el fin, medidas desde el centro del vano (mm). Con las dos en 0 se usa Length centrada.</summary>
        public double FromMm { get; set; } = 0;
        public double ToMm { get; set; } = 0;

        public BastonCfg Clone() => (BastonCfg)MemberwiseClone();

        [JsonIgnore] public bool IsTop => !string.Equals((Face ?? "").Trim(), "bottom", StringComparison.OrdinalIgnoreCase);

        /// <summary>0 inicio, 1 fin, 2 ambos, 3 centro, 4 tramo (orden del desplegable).</summary>
        [JsonIgnore]
        public int PositionIndex
        {
            get
            {
                string p = (Position ?? "").Trim().ToLowerInvariant();
                if (p == "start" || p == "inicio") return 0;
                if (p == "end" || p == "fin") return 1;
                if (p == "center" || p == "centro") return 3;
                return 2;
            }
        }

        public static readonly string[] Positions = { "start", "end", "both", "center" };
        public static readonly string[] PositionLabels = { "Inicio", "Fin", "Ambos extremos", "Centro del vano" };

        [JsonIgnore]
        public string Describe => (IsTop ? "superior" : "inferior") + " " + PositionLabels[PositionIndex].ToLowerInvariant();
    }

    /// <summary>Estribos rectangulares cerrados (uno por seccion, en el alma).</summary>
    public class StirrupCfg
    {
        public string BarTypeName { get; set; } = "";
        /// <summary>Nombre (o fragmento) del RebarHookType para los dos extremos del estribo ("135" suele bastar). Vacio = sin gancho.</summary>
        public string HookTypeName { get; set; } = "135";
        /// <summary>"left" o "right": lado hacia el que gira el gancho. Si el gancho queda fuera del hormigon, el plugin lo invierte solo.</summary>
        public string HookOrientation { get; set; } = "left";
        /// <summary>Distribucion desde cada apoyo como en los planos: "1@50, 8@100, R@200" (mm; ".05" se lee como metros).</summary>
        public string Distribution { get; set; } = "1@50, 8@100, R@200";
        /// <summary>Repetir los grupos fijos desde el otro extremo (confinamiento en los dos apoyos).</summary>
        public bool Symmetric { get; set; } = true;
        /// <summary>Desfase desde la cara de inicio a partir del cual se mide la distribucion (mm), por ejemplo media columna si la viga esta modelada de eje a eje.</summary>
        public double StartOffsetMm { get; set; } = 0;
        /// <summary>Idem desde la cara final.</summary>
        public double EndOffsetMm { get; set; } = 0;
    }

    public class AppConfig
    {
        /// <summary>Recubrimiento al estribo desde las caras del alma (mm).</summary>
        public double CoverMm { get; set; } = 40;

        public FaceCfg TopBars { get; set; } = new FaceCfg();
        public FaceCfg BottomBars { get; set; } = new FaceCfg();
        public LongitudinalCfg Longitudinal { get; set; } = new LongitudinalCfg();
        /// <summary>Capa intermedia: barras laterales por pares simetricos (una en cada rama del estribo), repartidas en el canto libre entre las capas superiores e inferiores.</summary>
        public SideBarsCfg SideBars { get; set; } = new SideBarsCfg();
        public List<BastonCfg> Bastones { get; set; } = new List<BastonCfg>();
        public StirrupCfg Stirrups { get; set; } = new StirrupCfg();

        /// <summary>
        /// Que geometria usar cuando la viga esta unida a otros elementos (columnas que la
        /// recortan en los extremos, losas que le quitan la parte alta):
        ///  "auto"     = la seccion se lee de la geometria completa de la familia y la longitud
        ///               del solido cortado (la viga entre caras de columna, con todo su canto);
        ///  "cut"      = solo el solido tal como lo ve Revit (cortado);
        ///  "original" = la geometria completa de la familia, tambien en longitud.
        /// </summary>
        public string JoinedGeometry { get; set; } = "auto";

        /// <summary>
        /// Plantilla del parametro Particion de cada barra. Comodines: {marca} (Marca del
        /// elemento; si esta vacia se usa el Id), {id}, {tipo}, {familia}, {conjunto}
        /// (nombre del juego de barras) y {cara} (superior / inferior / estribo).
        /// </summary>
        public string PartitionTemplate { get; set; } = "CC-{marca}";

        /// <summary>Espesor de las rebanadas de sondeo geometrico (mm).</summary>
        public double ProbeSliceMm { get; set; } = 10;

        /// <summary>Muestreo de la seccion a lo largo del eje: separacion entre estaciones (mm). Minimo 5 estaciones.</summary>
        public double PrismCheckStepMm { get; set; } = 250;

        /// <summary>Tolerancia geometrica al comparar secciones y agrupar coordenadas (mm).</summary>
        public double PrismCheckToleranceMm { get; set; } = 2;

        /// <summary>Angulo maximo (grados) que puede desviarse un borde de los ejes para seguir considerandolo rectilineo.</summary>
        public double RectilinearAngleDeg { get; set; } = 0.5;

        [JsonIgnore]
        public bool HookLeft => !string.Equals((Stirrups?.HookOrientation ?? "").Trim(), "right", StringComparison.OrdinalIgnoreCase);

        /// <summary>0 auto, 1 cortada, 2 completa (orden del desplegable).</summary>
        [JsonIgnore]
        public int JoinedIndex
        {
            get
            {
                string m = (JoinedGeometry ?? "").Trim().ToLowerInvariant();
                if (m == "cut" || m == "cortada") return 1;
                if (m == "original" || m == "completa") return 2;
                return 0;
            }
        }

        public static readonly string[] JoinedModes = { "auto", "cut", "original" };

        public FaceCfg Face(bool top) => top ? TopBars : BottomBars;

        /// <summary>Deja la configuracion en un estado coherente.</summary>
        public void Normalize()
        {
            if (TopBars == null) TopBars = new FaceCfg();
            if (BottomBars == null) BottomBars = new FaceCfg();
            if (Longitudinal == null) Longitudinal = new LongitudinalCfg();
            if (SideBars == null) SideBars = new SideBarsCfg();
            if (SideBars.BarTypeName == null) SideBars.BarTypeName = "";
            if (SideBars.Pairs < 0) SideBars.Pairs = 0;
            if (Bastones == null) Bastones = new List<BastonCfg>();
            if (Stirrups == null) Stirrups = new StirrupCfg();
            foreach (FaceCfg f in new[] { TopBars, BottomBars })
            {
                if (f.Layers == null) f.Layers = new List<LayerCfg>();
                f.Layers.RemoveAll(l => l == null);
                if (f.Layers.Count == 0) f.Layers.Add(new LayerCfg());
                if (f.Layers.Count > 3) f.Layers.RemoveRange(3, f.Layers.Count - 3);
                for (int i = 0; i < f.Layers.Count; i++)
                {
                    LayerCfg l = f.Layers[i];
                    if (l.BarTypeName == null) l.BarTypeName = "";
                    if (l.IntermediateBarTypeName == null) l.IntermediateBarTypeName = "";
                    if (i == 0 && l.Count < 2) l.Count = 2;
                    if (l.Count < 0) l.Count = 0;
                }
            }
            Bastones.RemoveAll(b => b == null);
            foreach (BastonCfg b in Bastones)
            {
                if (b.BarTypeName == null) b.BarTypeName = "";
                b.Face = b.IsTop ? "top" : "bottom";
                b.Position = BastonCfg.Positions[b.PositionIndex];
                if (b.GapMm < 0) b.GapMm = 0;
                if (b.Count < 1) b.Count = 1;
                if (string.IsNullOrWhiteSpace(b.Length)) b.Length = "1500";
                if (b.AnchorMm < 0) b.AnchorMm = 0;
                if (b.FromMm < 0) b.FromMm = 0;
                if (b.ToMm < 0) b.ToMm = 0;
            }
            if (Longitudinal.StartExtensionMm < 0) Longitudinal.StartExtensionMm = 0;
            if (Longitudinal.EndExtensionMm < 0) Longitudinal.EndExtensionMm = 0;
            if (Longitudinal.EndCoverMm < 0) Longitudinal.EndCoverMm = 0;
            if (Longitudinal.LegMm < 0) Longitudinal.LegMm = 0;
            if (Longitudinal.LayerClearMm < 0) Longitudinal.LayerClearMm = 0;
            if (Longitudinal.MinClearMm < 0) Longitudinal.MinClearMm = 0;
            if (Stirrups.BarTypeName == null) Stirrups.BarTypeName = "";
            if (Stirrups.HookTypeName == null) Stirrups.HookTypeName = "";
            Stirrups.HookOrientation = HookLeft ? "left" : "right";
            if (string.IsNullOrWhiteSpace(Stirrups.Distribution)) Stirrups.Distribution = "1@50, 8@100, R@200";
            if (Stirrups.StartOffsetMm < 0) Stirrups.StartOffsetMm = 0;
            if (Stirrups.EndOffsetMm < 0) Stirrups.EndOffsetMm = 0;
            JoinedGeometry = JoinedModes[JoinedIndex];
            if (CoverMm < 0) CoverMm = 0;
            if (ProbeSliceMm <= 0) ProbeSliceMm = 10;
            if (PrismCheckStepMm <= 0) PrismCheckStepMm = 250;
            if (PrismCheckToleranceMm <= 0) PrismCheckToleranceMm = 2;
            if (RectilinearAngleDeg <= 0) RectilinearAngleDeg = 0.5;
            if (string.IsNullOrWhiteSpace(PartitionTemplate)) PartitionTemplate = "CC-{marca}";
        }

        public static string ConfigPath()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(dir, "config.json");
        }

        private static JsonSerializerOptions ReadOptions() => new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static JsonSerializerOptions WriteOptions() => new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static AppConfig Load()
        {
            string path = ConfigPath();
            AppConfig cfg = File.Exists(path)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), ReadOptions()) ?? new AppConfig()
                : new AppConfig();
            cfg.Normalize();
            return cfg;
        }

        /// <summary>Guarda esta configuracion como config.json junto a la DLL (valores por defecto de la interfaz).</summary>
        public void Save(string path = null)
        {
            File.WriteAllText(path ?? ConfigPath(), JsonSerializer.Serialize(this, WriteOptions()));
        }

        /// <summary>Copia independiente, para que la interfaz edite sin tocar la configuracion cargada.</summary>
        public AppConfig Clone()
        {
            AppConfig c = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, WriteOptions()), ReadOptions())
                          ?? new AppConfig();
            c.Normalize();
            return c;
        }
    }
}
