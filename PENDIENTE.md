# Pendiente: probar en Revit 2027

## Integración ARBA-comun v1.0.0

Hecho en código (ver el README, sección *Contrato ARBA-comun*): submódulo `external/ARBA-comun`
compilado dentro del ensamblado, namespace `StripFootingRebar` (manifiesto actualizado),
partición `{categoria} - {prefijo}-{marca}` con la categoría del anfitrión real, `ARBA - Origen` /
`ARBA - Código` / `Metrado - Elemento` en cada conjunto, pregunta borrar / conservar / migrar al
rearmar, aviso de plantilla fuera del contrato y versión del contrato en la ventana y el informe.
`dotnet build -c Release` y `-c Debug` compilan sin errores ni avisos (SDK .NET 10, también en
Linux con `EnableWindowsTargeting`). Queda comprobar en Revit:

- [ ] Revit carga el add-in con el namespace nuevo (manifiesto `StripFootingRebar.RibbonApp` /
  `StripFootingRebar.ArmarCimientoCommand`); cinta: una sola pestaña ARBA, desplegable Acero con
  "Cimientos/Sobrecimientos" junto a "Zapatas" (icono propio: sección sobre terreno con estribo y
  dos capas).
- [ ] Armar un cimiento corrido (cimentación) marca `C1`: Partición `CIMIENTOS - CCO-C1`,
  `ARBA - Origen = CIMIENTOS CORRIDOS`, `ARBA - Código` = superior / inferior / estribo,
  `Metrado - Elemento = CIMIENTOS` (Propiedades del conjunto y Gestionar > Parámetros de
  proyecto: compartidos, de ejemplar, grupo Datos).
- [ ] Armar un sobrecimiento modelado como muro: `MUROS - CCO-<marca>`, `Metrado - Elemento = MUROS`.
- [ ] Revit en español: la partición se escribe (antes no).
- [ ] Rearmar: pregunta borrar / conservar; con "borrar" no quedan duplicados y el informe dice
  cuántos conjuntos se borraron; si un elemento se rechaza, su armadura anterior sigue ahí.
- [ ] Modelo con barras `CC-C1`: ofrece migrar; tras "Migrar sin rearmar", `CIMIENTOS - CCO-C1`
  (o `MUROS - …`) y origen relleno, sin barras nuevas; con "Borrar y rearmar" las `CC-C1` también
  desaparecen.
- [ ] Con el plugin de metrados: en "Metrado acero - Cimentaciones" conviven `CIMIENTOS - ZAP-…`,
  `CIMIENTOS - CCO-…` y `CIMIENTOS - BLQ-…`.
- [ ] `Application.SharedParametersFilename` del usuario sigue igual tras armar y no queda ningún
  `ARBA-comun-*.txt` en `%TEMP%`.
- [ ] Plantilla de partición que no empieza por `{categoria} - {prefijo}-`: la ventana avisa en
  rojo bajo el ejemplo y arma igual con la plantilla escrita.

Notas: ARBA-comun ya tiene las etiquetas `v1.0.1` y `v1.0.2` (cambios de `MAN`, `PesoProtegido` y
el comando de migración, nada que use este add-in); se queda en `v1.0.0` como pide el prompt y se
sube con `git -C external/ARBA-comun checkout vX.Y.Z` cuando toque.

## Correcciones anteriores (ramas ya integradas en `main`)

La corrección de las barras longitudinales que faltaban o quedaban cortas está terminada
en el código (ver el README: anclaje en el cimiento contiguo, barras que se parten en una
esquina, escalón de fondo entre tramos, patilla solo con prolongación o anclaje, anclaje
dibujado en los esquemas). Este repositorio no se ha podido compilar ni ejecutar fuera de
Windows + Revit, así que queda por comprobar en el modelo de la captura:

- [x] Compila con `dotnet build -c Debug` (SDK de .NET 10, paquetes `Nice3point.Revit.Api.*` 2027):
  comprobado en Linux al integrar ARBA-comun (sin errores ni avisos); la copia a la carpeta de
  add-ins se prueba en Windows.
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
