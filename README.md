# Armado automático de cimientos corridos y sobrecimientos — add-in Revit 2027

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
- Para anclar las barras corridas dentro de la columna usa la **prolongación en el
  inicio / fin**: esa parte de barra que sobresale del tramo no se comprueba contra el
  hormigón del muro.

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
   cualquier tramo y su última esquina queda sin barra pasante (las barras terminan con
   el recubrimiento de extremos en las dos caras de esa esquina).
4. Los tramos que quedan recorridos al revés se vuelven a leer con el **eje invertido**,
   así `u`, `v`, `w` siguen siempre el sentido del recorrido.
5. El recorrido tiene una cota `w` continua (0 en el inicio del primer tramo) y un perfil
   desarrollado (`BeamProfile.Concat`): los tramos van uno tras otro, con el mismo ancho
   de alma (si no, se rechaza) y con escalón en la unión si cambia el canto.

En la ventana cada recorrido es una fila (`[cadena 1: id > id > id]`), con su propia
distribución de estribos, y el alzado es el **desarrollo** del recorrido; las líneas
verticales del alzado marcan las esquinas.

## Qué cambia en el armado respecto a una viga

- **Barras corridas y bastones** en un recorrido con esquinas: cada barra se crea una a
  una (no como array) con la **normal vertical**, como una polilínea plana en planta que
  pasa por la intersección de sus dos líneas en cada esquina (la barra dobla siguiendo
  el recorrido a su distancia del borde). Las **patillas** se omiten en esas barras (no
  serían planas) y se avisa. En un recorrido de un solo tramo todo es como en una viga
  (arrays, patillas).
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

Mismas claves que el add-in de vigas; por defecto `"distribution": "R@200"` y
`"partitionTemplate": "CC-{marca}"`.

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
| `FootingChain.cs` | Recorrido: encadena los tramos por sus extremos, cota `w` continua, polilíneas de barra con esquinas, solidos de la cadena. |
| `BeamSection.cs` | Lectura del sólido (eje de cimentación de muro o curva de ubicación, eje invertible), partición en trozos del sólido cortado por columnas unidas, sección sintética de un recorrido. |
| `BeamProfile.cs` | Perfil por tramos y `Concat` de los perfiles de una cadena. Pura. |
| `BeamPlan.cs`, `StirrupLayout.cs`, `Rectilinear.cs` | Armado de la sección, distribución de estribos, geometría pura (iguales que en vigas). |
| `HostAnalysis.cs` | Resultado por elemento y agrupación en recorridos (`Chained`). |
| `RebarGenerator.cs` | Crea los `Rebar` tramo a tramo y a lo largo del recorrido, con las redes de seguridad contra la unión de sólidos. |
| `RebarOptionsWindow.cs`, `SectionPreview.cs`, `ElevationPreview.cs`, `BastonPreview.cs`, `RevitTheme.cs` | Ventana y esquemas. |
| `ArmarCimientoCommand.cs`, `RibbonApp.cs` | Comando externo y cinta. |

## Limitaciones conocidas

- Tramos rectos y horizontales; los cimientos encadenados tienen que tener el mismo
  ancho de alma.
- En un anillo cerrado la última esquina no lleva barra pasante.
- Sin patillas en las barras que doblan en las esquinas.
- No hace comprobaciones estructurales.
