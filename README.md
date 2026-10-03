# Armado automático de cimientos corridos y sobrecimientos — add-in Revit 2027

Genera la armadura de **cimientos corridos** (cimentaciones de muro o vigas de
cimentación) **siguiendo su recorrido**: los tramos rectos seleccionados se encadenan
por sus extremos, las barras longitudinales corridas doblan en cada esquina y los
estribos van tramo a tramo. La lectura de la geometría, la sección (capas, bastones,
laterales, estribo) y la ventana son las mismas que en el add-in de vigas
([Acero-vigas](https://github.com/Andy-rba30/Acero-vigas)); lo nuevo es el recorrido.

Comparte la pestaña **ARBA** y el desplegable **Acero** con los demás add-ins ARBA, y
sigue el **contrato [ARBA-comun](https://github.com/Andy-rba30/ARBA-comun)** (v1.0.0,
submódulo `external/ARBA-comun`): partición `CATEGORIA - CCO-marca`, parámetros compartidos
`ARBA - Origen` / `ARBA - Código` / `Metrado - Elemento`, borrar y rearmar sin duplicados y
migración de modelos con barras antiguas (ver [más abajo](#contrato-arba-comun-partición-origen-borrar-y-rearmar-migración)).
El ensamblado es `StripFootingRebar` y el namespace también `StripFootingRebar` (antes
`FootingRebar`, que chocaba con el add-in de zapatas).

## Qué elementos admite

- **Cimentaciones estructurales** (categoría *Structural Foundations*): cimentaciones de
  muro (el eje es el del muro que las lleva) o familias de cimiento con curva de
  ubicación recta.
- **Vigas de cimentación** modeladas como *Structural Framing*.
- **Suelos** (*Floors*, incluidas las losas de cimentación): un cimiento corrido dibujado
  con la herramienta Suelo. El suelo tiene que ser **estructural** (casilla *Estructural*)
  y de hormigón para que Revit admita armadura en él. Como un suelo no tiene curva de
  ubicación, el eje se lee de su contorno en planta; y si el contorno tiene esquinas (una
  L, una U, un anillo cerrado, un cruce...) el suelo se **parte en tramos rectos**
  (`FloorStrips`) que después se encadenan como si fueran cimientos separados. El
  contorno tiene que ser rectilíneo (bordes rectos y perpendiculares entre sí, sin arcos)
  y el suelo horizontal; los retales de menos de 100 mm de ancho se ignoran.
- **Muros** (*Walls*): sobrecimientos modelados como muro. El muro tiene que ser
  **estructural** (uso estructural portante) y de hormigón. El eje es su curva de
  ubicación, que tiene que ser recta; cada muro es un tramo y se encadena con los demás
  por sus extremos como cualquier otro cimiento.

Cada elemento tiene que ser un tramo **recto** de sección rectilínea (rectangular, en T
invertida, escalonada...) y se lee exactamente igual que una viga: rebanadas
perpendiculares al eje, tramos de sección constante o variable, geometría completa de
la familia si está unida.

### Columnas unidas con prioridad: sin armadura dentro de la columna

Si el sobrecimiento (o el cimiento) está **unido** a las columnas con *Unir geometría* y
la columna tiene prioridad (*Cambiar orden de unión*), la columna le quita al muro el
hormigón donde se cruzan y Revit devuelve el muro como un solo sólido con varios
**trozos sueltos**, uno entre cada dos columnas. El add-in respeta esa unión:

- El sólido se parte en sus trozos (`SolidUtils.SplitVolumes`) y se ordenan a lo largo
  del eje (`BeamSection.SplitAlongAxis`). Cada trozo se analiza y se arma como un
  **tramo aparte** (`[id nombre tramo 2/4]` en la ventana, con su propia distribución de
  estribos), con sus barras corridas y estribos solo donde hay hormigón del muro: dentro
  de la columna no se pone armadura del muro. Los retales de menos de 100 mm se ignoran.
- Dos trozos alineados a ambos lados de una columna **no se encadenan** aunque sus
  extremos estén cerca: para enlazar dos extremos hace falta además que los dos tramos
  se **toquen** (el eje de uno, prolongado 30 mm más allá de su cara, entra en el
  hormigón del otro). Así una esquina con columna tampoco se enlaza: cada muro termina
  en la cara de la columna.
- Con *Extremos contra otro cimiento* activado (`anchorInAdjacent`, ver más abajo), las
  barras corridas de cada trozo atraviesan la columna hasta su cara opuesta menos el
  recubrimiento de extremos, así los trozos de los dos lados se solapan dentro de la
  columna. Si lo desactivas, usa la **prolongación en el inicio / fin**: esa parte de
  barra que sobresale del tramo no se comprueba contra el hormigón del muro.

Con una **familia** de cimiento unida a columnas el comportamiento sigue siendo el de
siempre en modo *auto* / *completa* (la sección se lee de la geometría completa de la
familia y las barras pasan de largo); en modo *solo el sólido cortado* se parte en trozos
como un muro. Si el muro no está unido a la columna (o el muro tiene prioridad), el
sólido es de una pieza y las barras pasan por la columna.

## El recorrido (`FootingChain`)

1. Cada tramo armable da sus dos extremos de eje (centro del alma en cada cara
   extrema).
2. Dos extremos que están a menos de **una anchura de cimiento + 100 mm** (en planta y en
   cota) y cuyos tramos **se tocan** ahí (si entre las dos caras hay un hueco, una columna
   unida con prioridad u otro elemento, no es una esquina) se **enlazan**. Los enlaces se
   prueban de menor a mayor giro: en un nudo con más
   de dos extremos (una esquina a la que además llega un cimiento en T) se enlazan
   primero los dos tramos que menos giran, el cimiento que "pasa", y el otro empieza o
   termina ahí. Un cimiento que llega en T a mitad de otro no se enlaza con él: es un
   recorrido aparte que termina en la cara del primero (prolonga sus barras con la
   **prolongación en el fin** para anclarlas dentro).
3. Se recorren las cadenas desde un extremo libre; un anillo cerrado se recorre desde
   cualquier tramo y su última esquina es la **esquina de cierre**: ahí no pasa una barra
   continua, sino que los dos extremos de cada barra se anclan en el cimiento contiguo y se
   cruzan (ver más abajo).
4. Los tramos que quedan recorridos al revés se vuelven a leer con el **eje invertido**,
   así `u`, `v`, `w` siguen siempre el sentido del recorrido.
5. El recorrido tiene una cota `w` continua (0 en el inicio del primer tramo) y un perfil
   desarrollado (`BeamProfile.Concat`): los tramos van uno tras otro, con el mismo ancho
   de alma (si no, se rechaza) y con escalón en la unión si cambia el canto **o la cota
   de fondo** (el `v` de cada tramo se mide desde su propio fondo, así que el perfil del
   recorrido desplaza cada tramo lo que su fondo está más alto o más bajo que el del
   primero, `ChainSeg.DV`; las barras salvan el escalón con una bayoneta y los estribos
   van a la cota de su tramo).

En la ventana cada recorrido es una fila (`[cadena 1: id > id > id]`), con su propia
distribución de estribos, y el alzado es el **desarrollo** del recorrido; las líneas
verticales del alzado marcan las esquinas.

## Qué cambia en el armado respecto a una viga

- **Barras corridas y bastones** en un recorrido con esquinas: cada barra se crea una a
  una (no como array) con la **normal vertical**, como una polilínea plana en planta que
  pasa por la intersección de sus dos líneas en cada esquina (la barra dobla siguiendo
  el recorrido a su distancia del borde). En una unión de tramos alineados (un muro
  partido en una T) la barra sigue recta, sin vértice. Las **patillas** se omiten en esas
  barras (no serían planas) y se avisa. En un recorrido de un solo tramo todo es como en
  una viga (arrays, patillas).
- **Anclaje en el cimiento contiguo** (`anchorInAdjacent`, casilla *Extremos contra otro
  cimiento*, activado por defecto): en un extremo sin prolongación que llega a otro
  cimiento (en T, en una esquina que no sigue el recorrido o en la esquina que cierra un
  anillo), las barras corridas, las laterales y los bastones de extremo sin anclaje
  propio atraviesan ese hormigón y terminan en su cara opuesta menos el recubrimiento de
  extremos, en vez de quedarse en la cara del propio cimiento. Solo cuenta si el hormigón
  contiguo (cimentaciones, vigas, suelos, muros y columnas armables; en un suelo partido
  en tramos, también el resto del propio suelo) termina a menos de `max(2 m, 2 anchos)`;
  si sigue más allá no es un cimiento que cruza. En un array se usa la prolongación
  menor. Ese hormigón contiguo también cuenta en las redes de seguridad, y el resumen lo
  apunta en las `NOTAS`. Los esquemas del alzado y de los bastones dibujan el anclaje a
  trazos más allá de la cara (`ancla -300 mm`).
- **Barras que se parten en una esquina**: `Rebar.CreateFromCurves` devuelve `null` si el
  proyecto no tiene una forma de armadura con parámetros para tantos segmentos (una barra
  que da la vuelta a un recorrido con muchas esquinas), y una barra tampoco puede no ser
  plana (tramos a distinta cota de fondo: bayoneta más esquinas) ni cruzarse a sí misma
  (anillo cerrado, donde los dos extremos anclados se cruzan en la esquina de cierre). En
  esos casos la barra se parte en la esquina de más giro en planta más cercana a su mitad:
  cada trozo sigue recto pasada la esquina hasta la cara opuesta del tramo siguiente menos
  el recubrimiento de extremos (los dos trozos se cruzan ahí) y se vuelve a intentar con
  cada trozo, de forma recursiva; una barra recta se crea siempre. Se avisa en el resumen.
- **Patilla** (`legMm`): solo en un extremo prolongado más allá de la cara o anclado en el
  cimiento contiguo; sin apoyo donde doblarla no se pone.
- **Estribos**: cada estación se coloca en el sistema local del tramo que la contiene y
  se aloja en ese elemento; los arrays no cruzan una esquina.
- **Comprobaciones de seguridad**: cada barra se comprueba contra la **unión de los
  sólidos** de todos los tramos del recorrido (en una esquina la barra pasa de un
  cimiento al siguiente), con las fibras extremas tomadas según la dirección de cada
  trozo de barra. Si algo queda fuera del hormigón se deshace el recorrido entero.

## Empalmes por longitud comercial (`SpliceLayout`)

Como en el add-in de vigas, las barras corridas más largas que la **longitud comercial**
(9 m por defecto; 0 = sin empalmes) se parten en trozos solapados la **longitud de
empalme a tracción** de ACI 318-19 (`ld` de 25.4.2.3 con el factor 1.3 de barra alta,
clase B = 1.3 ld o clase A, mínimo 300 mm, o una longitud fija; `f'c` y `fy` en kg/cm²),
con el segundo trozo pegado por dentro y una bayoneta 1:6 para volver a la línea. Los
bastones no se empalman.

Lo propio del recorrido: aquí el "vano" es cada **tramo de la cadena** (cada elemento
entre columnas o esquinas, con su cota `w`), así que la zona de empalme se calcula tramo
a tramo: superiores en el tercio central de cada tramo e inferiores en sus cuartos
extremos fuera de `2h` de la cara del apoyo (ambas configurables; las laterales en el
tercio central). Los empalmes justos se reparten lo más uniformemente posible a lo largo
de la barra y cada uno se lleva a la zona permitida más cercana; si así no caben con la
barra comercial se reparten por igual y la fila del recorrido lo avisa. La longitud que
se empalma es la de la barra entera, anclaje en el cimiento contiguo incluido. Los trozos
doblan en las esquinas igual que la barra entera (cada trozo es su propio `Rebar`; como
el segundo trozo y los siguientes llevan la bayoneta del empalme, si además doblan en una
esquina no son planos y se parten en ella como se explica arriba), y en el alzado
desarrollado se ven con su etiqueta `empalme 1100`. La longitud de empalme de cada tipo
de barra en uso se muestra en la ventana, bajo los campos de empalme.

Todo lo demás (capas por cara con dos diámetros, capa intermedia de laterales, bastones
apilados o entre las corridas, selección especial de barras en la sección, ganchos con
inversión automática, distribución `1@50, 8@100, R@200` desde cada extremo, partición,
`config.json`) es idéntico al add-in de vigas: consulta su README.

## config.json

Mismas claves que el add-in de vigas (bloque `splices` incluido: `commercialLengthMm`,
`fcKgCm2`, `fyKgCm2`, `classB`, `fixedLengthMm`, `topZone`, `bottomZone`), más
`longitudinal.anchorInAdjacent` (anclar los extremos en el cimiento contiguo, `true` por
defecto); por defecto `"distribution": "R@200"` y
`"partitionTemplate": "{categoria} - {prefijo}-{marca}"` (la plantilla del contrato, ver la
sección siguiente; una plantilla que no empiece por `{categoria} - {prefijo}-` se arma igual
pero la ventana lo avisa).

## Contrato ARBA-comun: partición, origen, borrar y rearmar, migración

El código común se compila dentro de `StripFootingRebar.dll` desde el submódulo
`external/ARBA-comun` (`Arba.Comun.props`, etiqueta `v1.0.0`; no se modifica desde este
repo). Lo que aporta a este add-in:

**Partición de cada conjunto.** `{categoria} - {prefijo}-{marca}`: la categoría es la del
**anfitrión real** (es como agrupa el plugin de metrados), el prefijo `CCO` dice que lo armó
este add-in y la marca es el parámetro Marca del anfitrión (si está vacía, su Id). La cara no
va en la partición (una partición por elemento) sino en `ARBA - Código`:

| Anfitrión | Partición | `Metrado - Elemento` |
|-----------|-----------|----------------------|
| Cimentación estructural (cimiento corrido) marca `C1` | `CIMIENTOS - CCO-C1` | `CIMIENTOS` |
| Muro estructural (sobrecimiento) marca `SC1` | `MUROS - CCO-SC1` | `MUROS` |
| Suelo estructural marca `L1` (o sin marca, Id 123456) | `LOSAS - CCO-L1` (`LOSAS - CCO-123456`) | `LOSAS` |
| Viga de cimentación (armazón estructural) marca `VC1` | `VIGAS - CCO-VC1` | `VIGAS` |

En un recorrido con varios tramos cada barra toma la categoría y la marca del tramo en el que
Revit la crea (las corridas que cruzan tramos, en el primero). La partición se escribe en el
parámetro predefinido de Revit (`NUMBER_PARTITION_PARAM`), así que también funciona en Revit
en español (antes se buscaba `"Partition"` por nombre y no se escribía nada). Además de la
partición, cada conjunto lleva `ARBA - Origen = CIMIENTOS CORRIDOS`, `ARBA - Código =`
`superior` / `inferior` / `estribo` y `Metrado - Elemento` con la categoría. Los tres
parámetros compartidos (GUID fijos del contrato) se vinculan solos al armar
(`ArbaSharedParams.Ensure`, grupo *Datos*), sin tocar el archivo de parámetros compartidos del
usuario.

**Borrar y rearmar.** Antes de armar, el comando busca en cada anfitrión los conjuntos con
`ARBA - Origen = CIMIENTOS CORRIDOS` y las barras **anteriores al contrato** (partición
`CC-…` sin origen). Si hay algo, pregunta una vez:

- *Borrar la armadura del add-in y rearmar*: en cada anfitrión se migran primero las barras
  antiguas (para reconocerlas como propias), se borran los conjuntos del add-in y se arma de
  nuevo, todo dentro de la subtransacción del elemento: si el elemento no se puede armar, su
  armadura anterior se conserva. Sin duplicados.
- *Conservar lo que hay y armar encima*: no toca nada (queda duplicado).
- *Migrar las barras antiguas sin rearmar* (solo si las hay): reescribe la partición
  (`CC-C1` → `CIMIENTOS - CCO-C1`, o `MUROS - CCO-C1` si el anfitrión es un muro), rellena
  `ARBA - Origen`, `ARBA - Código` y `Metrado - Elemento` y no crea ninguna barra. El botón
  **Migrar particiones y origen** del plugin de metrados hace lo mismo para todo el modelo.

Las barras de otros add-ins (`ZAP-…`, `BLQ-…`) en el mismo anfitrión no se tocan nunca. El
informe final y el pie de la ventana muestran la versión del contrato con la que se compiló.

## Compilar e instalar

Requiere el SDK de .NET 10 y Revit 2027 (para 2025/2026 cambia el `TargetFramework` a
`net8.0-windows`, la versión de los paquetes `Nice3point.Revit.Api.*` y `RevitVersion`).
El código común viene en un **submódulo**, así que al clonar:

```
git clone --recurse-submodules https://github.com/Andy-rba30/Acero-cimientos-corridos
# o, en un clon ya hecho:
git submodule update --init
dotnet build -c Debug
```

Para subir de versión el común: `git -C external/ARBA-comun checkout vX.Y.Z` y commit del
puntero. El proyecto compila también fuera de Windows (`EnableWindowsTargeting`) para
comprobar la compilación; la copia a la carpeta de add-ins solo se hace en Windows.

En Debug la compilación copia `StripFootingRebar.dll`, `config.json` y
`StripFootingRebar.addin` a `%AppData%\Autodesk\Revit\Addins\2027\`. Al abrir Revit
aparece el botón **Cimientos/Sobrecimientos** (nombre interno `ARBA_Acero_Cimientos`) en el
desplegable **Acero** de la pestaña **ARBA** y el comando también en Complementos >
Herramientas externas. El manifiesto apunta a `StripFootingRebar.RibbonApp` y
`StripFootingRebar.ArmarCimientoCommand`.

La rama principal del repositorio es `main`; ahí está siempre la última versión.

## Estructura del código

| Archivo | Qué hace |
|---------|----------|
| `FootingChain.cs` | Recorrido: encadena los tramos por sus extremos, cota `w` continua, escalón de fondo de cada tramo, polilíneas de barra con esquinas (sin vértices colineales), solidos de la cadena. |
| `BeamSection.cs` | Lectura del sólido (eje de cimentación de muro o curva de ubicación, eje invertible), partición en trozos del sólido cortado por columnas unidas, sección sintética de un recorrido. |
| `BeamProfile.cs` | Perfil por tramos y `Concat` de los perfiles de una cadena. Pura. |
| `BeamPlan.cs`, `StirrupLayout.cs`, `Rectilinear.cs` | Armado de la sección, distribución de estribos, geometría pura (iguales que en vigas). |
| `SpliceLayout.cs` | Empalmes por traslape: longitud de empalme (ACI 318-19) por diámetro y reparto de los trozos con cada empalme en la zona de un tramo del recorrido. Pura. |
| `HostAnalysis.cs` | Resultado por elemento y agrupación en recorridos (`Chained`). |
| `RebarGenerator.cs` | Crea los `Rebar` tramo a tramo y a lo largo del recorrido, con las redes de seguridad contra la unión de sólidos. |
| `RebarOptionsWindow.cs`, `SectionPreview.cs`, `ElevationPreview.cs`, `BastonPreview.cs` | Ventana y esquemas (tema oscuro `RevitTheme` del común). |
| `ArmarCimientoCommand.cs`, `RibbonApp.cs` | Comando externo (parámetros del contrato, borrar y rearmar, migración) y botón de la cinta con su icono. |
| `external/ARBA-comun/src/` (submódulo) | Contrato (`ArbaContract`), partición (`ArbaPartition`, `PartitionName`), parámetros compartidos (`ArbaSharedParams`), origen (`ArbaOrigin`), migración (`ArbaMigration`), cinta (`ArbaRibbon`), tema (`RevitTheme`) y nombres de tipo (`NameMatch`). |

## Limitaciones conocidas

- Tramos rectos y horizontales; los cimientos encadenados tienen que tener el mismo
  ancho de alma.
- En un anillo cerrado no hay una barra continua que dé toda la vuelta: cada barra se
  parte en una esquina y los trozos se cruzan ahí y en la esquina de cierre (anclados en
  el cimiento contiguo); sin anclaje terminan en la cara con el recubrimiento de extremos.
- Sin patillas en las barras que doblan en las esquinas.
- No hace comprobaciones estructurales.
