# Pendiente: probar en Revit 2027

La corrección de las barras longitudinales que faltaban o quedaban cortas está terminada
en el código (ver el README: anclaje en el cimiento contiguo, barras que se parten en una
esquina, escalón de fondo entre tramos, patilla solo con prolongación o anclaje, anclaje
dibujado en los esquemas). Este repositorio no se ha podido compilar ni ejecutar fuera de
Windows + Revit, así que queda por comprobar en el modelo de la captura:

- [ ] Compila con `dotnet build -c Debug` (SDK de .NET 10, paquetes `Nice3point.Revit.Api.*` 2027).
- [ ] Perímetro cerrado con muros partidos en las T: ya no aparece `INCOMPLETO` y las barras
  llegan a la cara opuesta del cimiento al que llegan (aviso de "barras partidas en una
  esquina" en el resumen; los trozos se cruzan en la esquina partida y en la de cierre).
- [ ] Cimientos en T: las barras del que llega entran hasta la cara opuesta del otro menos
  el recubrimiento de extremos (`NOTAS: inicio/fin anclado en el cimiento contiguo`).
- [ ] Recorrido con tramos a distinta cota de fondo (mismo canto): el alzado desarrollado
  muestra el escalón, las barras lo salvan con bayoneta y los estribos van a la cota de
  cada tramo.
- [ ] Los esquemas del alzado y de los bastones dibujan a trazos el anclaje (`ancla -300 mm`)
  igual que lo crea el generador.
- [ ] Con `legMm > 0` y sin prolongación ni anclaje en un extremo, ese extremo va sin patilla.

En `main` se han integrado además las ramas de suelos / muros / columnas unidas y la de
empalmes por longitud comercial, que tampoco se han compilado juntas. Comprobar también:

- [ ] Recorrido largo con esquinas y empalmes (`commercialLengthMm` 9000): los trozos
  empalmados que doblan en una esquina se parten ahí (aviso en el resumen) y el alzado
  muestra `empalme ...` donde se crean.
- [ ] Muro (sobrecimiento) partido por columnas unidas con prioridad: con *Extremos contra
  otro cimiento* las barras de cada trozo atraviesan la columna hasta su cara opuesta menos
  el recubrimiento; desactivado, terminan en la cara de la columna.
- [ ] Suelo con forma de T: el tramo que llega en T ancla sus barras en el otro tramo del
  mismo suelo.
