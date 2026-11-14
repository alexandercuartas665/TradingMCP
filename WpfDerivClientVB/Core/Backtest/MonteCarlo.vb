' ============================================================
' MonteCarlo.vb
'
' Pruebas de estres sobre los resultados de una estrategia:
'
'  1) Permutacion: se baraja el ORDEN de las operaciones miles de
'     veces. El resultado total no cambia, pero la caida maxima y la
'     peor racha si: muestra que tan mal puede ir el camino aunque
'     el resultado final sea el mismo.
'  2) Bootstrap: se remuestrean las operaciones con reemplazo.
'     Da la distribucion del resultado total y la probabilidad de
'     terminar ganando con una muestra de ese tamano.
'  3) Ruido en precios: se vuelve a simular la estrategia con los
'     precios ligeramente alterados. Si el acierto se derrumba con
'     un ruido pequeno, la "ventaja" dependia de detalles exactos
'     del pasado (sobreajuste).
'
' Todo usa semilla fija: los resultados son reproducibles.
' ============================================================
Imports System.Collections.Generic
Imports System.Linq
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Estrategias

Namespace WpfDerivClientVB.Backtesting

    Public Module MonteCarlo

        ''' <summary>Permutacion + bootstrap sobre la lista de operaciones (una cuenta).</summary>
        Public Function AnalizarOperaciones(ops As List(Of BacktestOperacion), Optional iteraciones As Integer = 5000,
                                            Optional semilla As Integer = 12345) As JObject
            Dim pnl = ops.Select(Function(o) CDbl(o.ProfitLoss)).ToArray()
            Dim n = pnl.Length
            If n = 0 Then Return New JObject From {{"ok", False}, {"error", "Sin operaciones."}}
            Dim rnd As New Random(semilla)

            ' Real (orden historico)
            Dim ddReal = CaidaMaxima(pnl), rachaReal = PeorRacha(pnl)

            ' 1) Permutaciones
            Dim dds(iteraciones - 1) As Double, rachas(iteraciones - 1) As Double
            Dim copia = CType(pnl.Clone(), Double())
            For it = 0 To iteraciones - 1
                For i = n - 1 To 1 Step -1
                    Dim j = rnd.Next(i + 1)
                    Dim t = copia(i) : copia(i) = copia(j) : copia(j) = t
                Next
                dds(it) = CaidaMaxima(copia)
                rachas(it) = PeorRacha(copia)
            Next

            ' 2) Bootstrap
            Dim totales(iteraciones - 1) As Double, wins(iteraciones - 1) As Double
            For it = 0 To iteraciones - 1
                Dim s = 0.0, w = 0
                For k = 1 To n
                    Dim v = pnl(rnd.Next(n))
                    s += v
                    If v > 0 Then w += 1
                Next
                totales(it) = s
                wins(it) = w / CDbl(n)
            Next

            Return New JObject From {
                {"ok", True},
                {"operaciones", n},
                {"iteraciones", iteraciones},
                {"resultado_total_real", Math.Round(pnl.Sum(), 2)},
                {"orden", New JObject From {
                    {"caida_maxima_real", Math.Round(ddReal, 2)},
                    {"caida_maxima_p50", Math.Round(Percentil(dds, 50), 2)},
                    {"caida_maxima_p95", Math.Round(Percentil(dds, 95), 2)},
                    {"peor_racha_real", rachaReal},
                    {"peor_racha_p50", Percentil(rachas, 50)},
                    {"peor_racha_p95", Percentil(rachas, 95)}
                }},
                {"bootstrap", New JObject From {
                    {"resultado_p5", Math.Round(Percentil(totales, 5), 2)},
                    {"resultado_p50", Math.Round(Percentil(totales, 50), 2)},
                    {"resultado_p95", Math.Round(Percentil(totales, 95), 2)},
                    {"prob_terminar_ganando", Math.Round(totales.Where(Function(x) x > 0).Count() / CDbl(iteraciones), 4)},
                    {"win_rate_p2_5", Math.Round(Percentil(wins, 2.5), 4)},
                    {"win_rate_p97_5", Math.Round(Percentil(wins, 97.5), 4)}
                }}
            }
        End Function

        ''' <summary>
        ''' Re-simula la estrategia con ruido gaussiano en los precios. El ruido de cada
        ''' precio es nivel x (rango mediano de vela) x N(0,1). nivel=0 reproduce el base.
        ''' </summary>
        Public Function RuidoEnPrecios(velas As List(Of CandleModel), estrategia As IEstrategia, cfg As BacktestConfig,
                                       Optional corridas As Integer = 30, Optional nivel As Double = 0.1,
                                       Optional semilla As Integer = 777) As JObject
            Dim cfgU = WalkForward.ClonarConfig(cfg)
            cfgU.UnaPosicionALaVez = True
            Dim baseSim = BacktestMotor.Simular(velas, estrategia, cfgU)
            Dim baseWin = TasaAcierto(baseSim.Operaciones)

            Dim rangos = velas.Select(Function(v) CDbl(Math.Abs(v.High - v.Low))).Where(Function(r) r > 0).OrderBy(Function(r) r).ToList()
            Dim rangoMediano = If(rangos.Count > 0, rangos(rangos.Count \ 2), 0.0)
            Dim sigma = nivel * rangoMediano
            Dim rnd As New Random(semilla)

            Dim winRates As New List(Of Double)(), pnls As New List(Of Double)(), opsN As New List(Of Integer)()
            For k = 1 To corridas
                Dim ruidosas As New List(Of CandleModel)(velas.Count)
                For Each v In velas
                    Dim o = CDbl(v.OpenPrice) + sigma * Normal(rnd)
                    Dim c = CDbl(v.ClosePrice) + sigma * Normal(rnd)
                    Dim h = Math.Max(CDbl(v.High) + sigma * Normal(rnd), Math.Max(o, c))
                    Dim l = Math.Min(CDbl(v.Low) + sigma * Normal(rnd), Math.Min(o, c))
                    ruidosas.Add(New CandleModel With {.Epoch = v.Epoch, .OpenTime = v.OpenTime,
                                 .OpenPrice = CDec(o), .High = CDec(h), .Low = CDec(l), .ClosePrice = CDec(c)})
                Next
                Dim sim = BacktestMotor.Simular(ruidosas, estrategia, cfgU)
                winRates.Add(TasaAcierto(sim.Operaciones))
                pnls.Add(CDbl(sim.Operaciones.Sum(Function(x) x.ProfitLoss)))
                opsN.Add(sim.Operaciones.Count)
            Next

            Dim be = Estadistica.PuntoEquilibrio(cfg.PayoutPct)
            Dim media = winRates.Average()
            Dim desv = Math.Sqrt(winRates.Select(Function(x) (x - media) ^ 2).Sum() / Math.Max(1, winRates.Count - 1))
            Return New JObject From {
                {"ok", True},
                {"corridas", corridas}, {"nivel_ruido", nivel},
                {"ruido_sigma_precio", Math.Round(sigma, 6)},
                {"base_operaciones", baseSim.Operaciones.Count},
                {"base_win_rate", Math.Round(baseWin, 4)},
                {"ruido_win_rate_media", Math.Round(media, 4)},
                {"ruido_win_rate_desv", Math.Round(desv, 4)},
                {"ruido_win_rate_min", Math.Round(winRates.Min(), 4)},
                {"ruido_win_rate_max", Math.Round(winRates.Max(), 4)},
                {"ruido_operaciones_media", Math.Round(opsN.Average(), 1)},
                {"ruido_pnl_media", Math.Round(pnls.Average(), 2)},
                {"corridas_sobre_equilibrio", winRates.Where(Function(x) x > be).Count()}
            }
        End Function

        ' ── Helpers ──────────────────────────────────────────────

        Friend Function CaidaMaxima(pnl As Double()) As Double
            Dim acum = 0.0, pico = 0.0, dd = 0.0
            For Each v In pnl
                acum += v
                If acum > pico Then pico = acum
                If pico - acum > dd Then dd = pico - acum
            Next
            Return dd
        End Function

        Friend Function PeorRacha(pnl As Double()) As Integer
            Dim actual = 0, peor = 0
            For Each v In pnl
                If v < 0 Then
                    actual += 1
                    If actual > peor Then peor = actual
                Else
                    actual = 0
                End If
            Next
            Return peor
        End Function

        Friend Function Percentil(valores As Double(), p As Double) As Double
            If valores.Length = 0 Then Return 0
            Dim orden = valores.OrderBy(Function(x) x).ToArray()
            Dim pos = (p / 100.0) * (orden.Length - 1)
            Dim lo = CInt(Math.Floor(pos)), hi = CInt(Math.Ceiling(pos))
            Return orden(lo) + (orden(hi) - orden(lo)) * (pos - lo)
        End Function

        Private Function TasaAcierto(ops As List(Of BacktestOperacion)) As Double
            If ops.Count = 0 Then Return 0
            Return ops.Where(Function(o) o.Estado = "Ganado").Count() / CDbl(ops.Count)
        End Function

        ''' <summary>Normal estandar (Box-Muller).</summary>
        Private Function Normal(rnd As Random) As Double
            Dim u1 = 1.0 - rnd.NextDouble()
            Dim u2 = rnd.NextDouble()
            Return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2)
        End Function

    End Module

End Namespace
