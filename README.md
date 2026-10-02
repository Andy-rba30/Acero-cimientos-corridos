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
barra comercial se reparten por igual y la fila del recorrido lo avisa. Los trozos
doblan en las esquinas igual que la barra entera (cada trozo es su propio `Rebar`), y
en el alzado desarrollado se ven con su etiqueta `empalme 1100`. La longitud de empalme
de cada tipo de barra en uso se muestra en la ventana, bajo los campos de empalme.

Todo lo demás (capas por cara con dos diámetros, capa intermedia de laterales, bastones
apilados o entre las corridas, selección especial de barras en la sección, ganchos con
inversión automática, distribución `1@50, 8@100, R@200` desde cada extremo, partición,
`config.json`) es idéntico al add-in de vigas: consulta su README.

## config.json

Mismas claves que el add-in de vigas (bloque `splices` incluido: `commercialLengthMm`,
`fcKgCm2`, `fyKgCm2`, `classB`, `fixedLengthMm`, `topZone`, `bottomZone`); por defecto
`"distribution": "R@200"` y `"partitionTemplate": "CC-{marca}"`.

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
| `BeamSection.cs` | Lectura del sólido (eje de cimentación de muro o curva de ubicación, eje invertible), sección sintética de un recorrido. |
| `BeamProfile.cs` | Perfil por tramos y `Concat` de los perfiles de una cadena. Pura. |
| `BeamPlan.cs`, `StirrupLayout.cs`, `Rectilinear.cs` | Armado de la sección, distribución de estribos, geometría pura (iguales que en vigas). |
| `SpliceLayout.cs` | Empalmes por traslape: longitud de empalme (ACI 318-19) por diámetro y reparto de los trozos con cada empalme en la zona de un tramo del recorrido. Pura. |
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
