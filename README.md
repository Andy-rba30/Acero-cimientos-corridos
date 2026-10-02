# Armado automático de cimientos corridos — add-in Revit 2027

Genera la armadura de **cimientos corridos** (cimentaciones de muro o vigas de
cimentación) **siguiendo su recorrido**: los tramos rectos seleccionados se encadenan
por sus extremos, las barras longitudinales corridas doblan en cada esquina y los
estribos van tramo a tramo. La lectura de la geometría, la sección (capas, bastones,
laterales, estribo) y la ventana son las mismas que en el add-in de vigas
([Acero-vigas](https://github.com/Andy-rba30/Acero-vigas)); lo nuevo es el recorrido.

Comparte la pestaña **ARBA** y el desplegable **Acero** con los add-ins de columnas,
vigas y muros de contención.

## Qué elementos admite

- **Cimentaciones estructurales** (categoría *Structural Foundations*): cimentaciones de
  muro (el eje es el del muro que las lleva) o familias de cimiento con curva de
  ubicación recta.
- **Vigas de cimentación** modeladas como *Structural Framing*.

Cada elemento tiene que ser un tramo **recto** de sección rectilínea (rectangular, en T
invertida, escalonada...) y se lee exactamente igual que una viga: rebanadas
perpendiculares al eje, tramos de sección constante o variable, geometría completa de
la familia si está unida.

## El recorrido (`FootingChain`)

1. Cada tramo armable da sus dos extremos de eje (centro del alma en cada cara
   extrema).
2. Dos extremos que están a menos de **una anchura de cimiento + 100 mm** (en planta y en
   cota) se **enlazan**. Los enlaces se prueban de menor a mayor giro: en un nudo con más
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
  contiguo (cimentaciones, vigas y columnas armables) termina a menos de `max(2 m, 2
  anchos)`; si sigue más allá no es un cimiento que cruza. En un array se usa la
  prolongación menor. Ese hormigón contiguo también cuenta en las redes de seguridad, y
  el resumen lo apunta en las `NOTAS`. Los esquemas del alzado dibujan el anclaje a
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

Todo lo demás (capas por cara con dos diámetros, capa intermedia de laterales, bastones
apilados o entre las corridas, selección especial de barras en la sección, ganchos con
inversión automática, distribución `1@50, 8@100, R@200` desde cada extremo, partición,
`config.json`) es idéntico al add-in de vigas: consulta su README.

## config.json

Mismas claves que el add-in de vigas, más `longitudinal.anchorInAdjacent` (anclar los
extremos en el cimiento contiguo, `true` por defecto); por defecto `"distribution":
"R@200"` y `"partitionTemplate": "CC-{marca}"`.

## Compilar e instalar

Requiere el SDK de .NET 10 y Revit 2027 (para 2025/2026 cambia el `TargetFramework` a
`net8.0-windows` y la versión de los paquetes `Nice3point.Revit.Api.*`).

```
dotnet build -c Debug
```

En Debug la compilación copia `StripFootingRebar.dll`, `config.json` y
`StripFootingRebar.addin` a `%AppData%\Autodesk\Revit\Addins\2027\`. Al abrir Revit
aparece el botón **Cimientos** en el desplegable **Acero** de la pestaña **ARBA** y el
comando también en Complementos > Herramientas externas.

## Estructura del código

| Archivo | Qué hace |
|---------|----------|
| `FootingChain.cs` | Recorrido: encadena los tramos por sus extremos, cota `w` continua, escalón de fondo de cada tramo, polilíneas de barra con esquinas (sin vértices colineales), solidos de la cadena. |
| `BeamSection.cs` | Lectura del sólido (eje de cimentación de muro o curva de ubicación, eje invertible), sección sintética de un recorrido. |
| `BeamProfile.cs` | Perfil por tramos y `Concat` de los perfiles de una cadena. Pura. |
| `BeamPlan.cs`, `StirrupLayout.cs`, `Rectilinear.cs` | Armado de la sección, distribución de estribos, geometría pura (iguales que en vigas). |
| `HostAnalysis.cs` | Resultado por elemento y agrupación en recorridos (`Chained`). |
| `RebarGenerator.cs` | Crea los `Rebar` tramo a tramo y a lo largo del recorrido, con las redes de seguridad contra la unión de sólidos. |
| `RebarOptionsWindow.cs`, `SectionPreview.cs`, `ElevationPreview.cs`, `BastonPreview.cs`, `RevitTheme.cs` | Ventana y esquemas. |
| `ArmarCimientoCommand.cs`, `RibbonApp.cs` | Comando externo y cinta. |

## Limitaciones conocidas

- Tramos rectos y horizontales; los cimientos encadenados tienen que tener el mismo
  ancho de alma.
- En un anillo cerrado no hay una barra continua que dé toda la vuelta: cada barra se
  parte en una esquina y los trozos se cruzan ahí y en la esquina de cierre (anclados en
  el cimiento contiguo); sin anclaje terminan en la cara con el recubrimiento de extremos.
- Sin patillas en las barras que doblan en las esquinas.
- No hace comprobaciones estructurales.
