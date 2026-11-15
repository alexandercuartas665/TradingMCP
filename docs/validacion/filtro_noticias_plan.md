# Prueba del filtro de noticias (calendario economico)

Plan **pre-registrado** el 2026-11-15, antes de mirar ninguna operacion. Pregunta: las
operaciones que estan abiertas durante una noticia de alto impacto de EE. UU. salen peor que
el resto? Si es asi, conviene un filtro que no abra contratos cerca de esas noticias.

## Calendario de eventos (`eventos_eeuu_2003_2024.json`)

- **NFP** (empleo de EE. UU., 8:30 hora de Nueva York): regla de la BLS, tercer viernes
  despues de la semana (domingo a sabado) que contiene el dia 12. Excepciones conocidas de la
  BLS: si cae el 3 o 4 de julio (feriado) se publica el jueves; si cae del 1 al 3 de enero se
  publica una semana despues; cierre de gobierno de 2013 (22-oct y 8-nov). 259 eventos.
- **FOMC** (decision de tipos de la Fed): reuniones programadas segun federalreserve.gov
  (paginas historicas 2003-2020 y calendario 2021+). Anuncio el ultimo dia; se toma la franja
  12:30-14:15 hora de Nueva York (comunicado de 12:30 en 2011-2012, de 14:15 hasta 2012 y de
  14:00 desde 2013). 172 eventos.
- Hora de Nueva York a UTC con el horario de verano de EE. UU. (reglas de 2003-2006 y 2007+).
- **Verificacion del calendario** (hecha antes de mirar operaciones, solo con velas): el rango de
  la vela de 1h del anuncio frente a la mediana de la misma hora en los 20 dias habiles
  anteriores. FOMC: mediana x5,0 (EUR/USD). NFP: mediana x2,5. Las excepciones de enero, julio
  y 2013 se agregaron porque la verificacion mostro esas fechas sin movimiento y coinciden con
  la practica publicada por la BLS. El calendario queda fijo desde aqui.

No se incluyen IPC ni bancos centrales europeos: sus fechas no siguen una regla y no hay una
fuente historica fiable sin registro. Si el filtro sirve con NFP y FOMC, se agregan despues.

## Operaciones

- 60 configuraciones en mercados con dolar (EUR/USD, GBP/USD, USD/JPY, USD/CHF, oro) a 1h y 4h:
  las 14 de divisas/oro de la bateria del historico largo y las 46 de las ideas 1-5 y 10-20
  del lote 2 (las de 1d quedan fuera). Mismos parametros que en esas pruebas.
- Fuente Dukascopy, 2003-05-01 a 2024-03-31. Una posicion a la vez.
- Una operacion esta **expuesta** si sigue abierta en algun momento de la ventana
  [inicio del evento - 30 min, fin del evento + 30 min].

## Que se mide y que decide

1. **Binarias** (principal): acierto de las expuestas frente a las no expuestas, sumando todo.
   El filtro se recomienda si las expuestas aciertan menos con **z <= -2,0** (prueba de dos
   proporciones) **y** si en la mayoria de las configuraciones con >= 20 expuestas el acierto
   expuesto es menor (prueba de signos, p < 0,05). Se informa cuanto subiria el acierto total
   al filtrar y cuantas operaciones se pierden.
2. **Multiplicadores** (descriptivo): movimiento medio absoluto del precio durante la operacion,
   expuestas frente a no expuestas, y el percentil 95. Si las expuestas se mueven >= 1,5 veces
   mas, el filtro se recomienda para multiplicadores aunque no cambie el acierto.
3. Desglose por tipo de evento (NFP / FOMC), solo informativo.

Nada de esto convierte una estrategia sin ventaja en rentable: el filtro solo evita perder de
mas. Si el resultado es nulo, no se construye el filtro.

## Resultado (2026-11-15)

Detalle en `filtro_noticias_2026-11-15.txt` / `.json`.

- **Binarias: sin efecto.** Expuestas 4.609 ops con 50,38% de acierto frente a 186.247 con
  50,48% (diferencia -0,10 pts, z -0,13). Peor en 13 de 35 configuraciones (p signos 0,96).
  Filtrar quitaria el 2,4% de las operaciones sin cambiar el acierto. NFP 49,5%, FOMC 51,9%.
  **No se construye el filtro para binarias.**
- **Multiplicadores: filtro recomendado.** Durante una noticia el precio se mueve 1,62 veces mas
  (media 0,278% frente a 0,172%; percentil 95: 0,88% frente a 0,54%). EUR/USD x1,63, GBP/USD
  x1,41, USD/JPY x1,69, oro x1,29. Si se operan multiplicadores en divisas, hay que bloquear
  las ventanas de NFP y FOMC.

Nota: la primera corrida tenia un error de lectura de fechas (PowerShell 7 convierte las fechas
del JSON y al pasarlas por texto se invertian dia y mes; salian 95 expuestas). Se corrigio el
script, no el plan, y se volvio a correr; las cifras de arriba son las de la corrida corregida.
