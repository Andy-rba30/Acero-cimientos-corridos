# Corrección en curso: barras longitudinales que faltan o quedan cortas

## Diagnóstico

1. **Barras que no se colocan** (recorridos con esquinas). `Rebar.CreateFromCurves`
   **devuelve `null`** sin lanzar excepción cuando no puede dar forma a la barra. Según la
   documentación de la API de Revit 2027, solo crea una forma nueva si el proyecto ya tiene
   formas con **parámetros suficientes para todos los segmentos**. Una barra que da la vuelta
   a un recorrido con muchas esquinas no cumple eso. Antes se apuntaba como "INCOMPLETO
   ... ()" y la barra no se creaba, aunque los estribos sí. También fallaban los vértices de
   **tramos alineados** (muro partido en una T): la barra tenía dos segmentos seguidos en la
   misma recta.
2. **Barras cortas**: en un extremo que llega en T a otro cimiento, o en la esquina que
   cierra un anillo, las barras terminaban en la cara del propio cimiento menos el
   recubrimiento, sin entrar en el cimiento al que llegan.

## Hecho (compila, falta probar en Revit)

- `FootingChain.Polyline`: en una unión de tramos alineados ya no se pone vértice. Los giros
  de menos de 2° se tratan como alineados y la nueva función `Clean` quita los vértices
  colineales.
- `RebarGenerator.PlaceChainBar`:
  - Limpia la polilínea y la ajusta a un plano exacto (`PlaneNormal`).
  - Si Revit no puede crear la barra, o la barra no es plana, la parte en la esquina de más
    giro más cercana a la mitad. Cada trozo sigue recto hasta la cara opuesta del tramo
    siguiente, menos el recubrimiento de extremos (`Reach`).
  - Lo hace de forma recursiva: una barra recta se crea siempre.
- `TryPlace`: igual que `Place`, pero devuelve el fallo de Revit para poder reintentar. El
  mensaje ya no sale vacío.
- Anclaje en el cimiento contiguo (`EndReach` y `NearbySolids`):
  - En cada extremo sin prolongación, las barras corridas, las laterales y los bastones de
    extremo con anclaje 0 atraviesan el hormigón contiguo hasta su cara opuesta, menos el
    recubrimiento.
  - Solo cuenta si ese hormigón termina a menos de `max(2 m, 2 anchos)`.
  - En un array se usa la prolongación menor.
- `BeamSection.ExtraSolids`: el hormigón contiguo (cimentaciones, vigas y columnas
  armables cerca de los extremos y de las uniones) también cuenta en las redes de
  seguridad.
- Nueva opción `longitudinal.anchorInAdjacent` (por defecto `true`):
  - En `config.json` y en `AppConfig`.
  - Casilla "Extremos contra otro cimiento" en la ventana.
- Resumen: nuevas "NOTAS" con lo que se ha anclado; aviso cuando se parten barras.

## Falta para terminar

- [ ] **Probar en Revit 2027** con el modelo de la captura:
  - Perímetro cerrado con muros partidos en las T.
  - Cimientos en T.
  - Comprobar que ya no aparece "INCOMPLETO" y que las barras llegan a la cara opuesta del
    cimiento al que llegan.
- [ ] Revisar el **anillo cerrado**: con el anclaje, las barras interiores del inicio y del
  fin se cruzan en la esquina de cierre (es la misma barra). Si Revit la acepta, queda así;
  si no, el troceo la parte. Decidir si conviene partirla siempre ahí.
- [ ] Recorridos con tramos a **distinta cota de fondo** (mismo canto): el `v` de cada tramo
  se mide desde su propio fondo, así que `BeamProfile.Concat` no ve el escalón. Ahora la
  barra se trocea por no ser plana, pero lo correcto es desplazar el perfil de cada tramo
  por su diferencia de cota en `Concat` y en `FootingChain.World`, y en los estribos.
- [ ] `ElevationPreview` / `BastonPreview`: no dibujan la prolongación automática del
  anclaje (solo afecta al esquema).
- [ ] Actualizar el `README.md`: anclaje en el cimiento contiguo, troceo de barras en las
  esquinas, nueva clave `anchorInAdjacent`, y la limitación de "En un anillo cerrado la
  última esquina no lleva barra pasante".
- [ ] Opcional: la patilla (`legMm`) se pone en los extremos aunque no haya prolongación (ya
  pasaba antes); valorar exigir prolongación o anclaje.
