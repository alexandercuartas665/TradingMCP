' ============================================================
' WalkForward.vb
'
' Walk-forward: divide el historico en ventanas sucesivas
' [entrenamiento | prueba]. En cada ventana se elige el umbral de
' confianza SOLO con el entrenamiento y se aplica a la prueba que
' viene despues. Se suman unicamente los resultados de prueba:
' es lo mas parecido a operar la estrategia re-optimizandola cada
' cierto tiempo, sin mirar el futuro.
'
' Si en el entrenamiento ningun umbral alcanza el minimo de
' operaciones o ninguno es rentable, esa ventana no opera.
' ============================================================
Imports System.Collections.Generic
Imports System.Linq
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Estrategias

Namespace WpfDerivClientVB.Backtesting

    Public Module WalkForward

        ''' <summary>
        ''' Ejecuta el walk-forward. cfgBase aporta symbol, timeframe, duracion, payout y
        ''' ventana de indicadores (se fuerza UnaPosicionALaVez = True).
        ''' </summary>
        Public Function Ejecutar(velas As List(Of CandleModel), estrategia As IEstrategia, cfgBase As BacktestConfig,
                                 grillaConfianza As IEnumerable(Of Integer),
                                 diasEntrenamiento As Integer, diasPrueba As Integer,
                                 Optional minOpsEntrenamiento As Integer = 10,
                                 Optional corteEpoch As Long = 0) As JObject
            If velas Is Nothing OrElse velas.Count = 0 Then Return New JObject From {{"ok", False}, {"error", "Sin velas."}}
            Dim grilla = grillaConfianza.Distinct().OrderBy(Function(c) c).ToList()
            Dim segTrain As Long = CLng(diasEntrenamiento) * 86400L
            Dim segTest As Long = CLng(diasPrueba) * 86400L
            Dim inicio = velas.First().Epoch
            Dim fin = velas.Last().Epoch

            Dim ventanas As New JArray()
            Dim opsPrueba As New List(Of BacktestOperacion)()
            Dim testDesde = inicio + segTrain
            Do While testDesde < fin
                Dim testHasta = Math.Min(testDesde + segTest, fin + 1)
                Dim trainDesde = testDesde - segTrain

                ' 1) Elegir umbral con el entrenamiento (maximo resultado con muestra minima)
                Dim mejorConf As Integer = -1
                Dim mejorPnl As Decimal = 0D
                Dim mejorOps As Integer = 0, mejorWin As Double = 0
                For Each conf In grilla
                    Dim ops = SimularRango(velas, estrategia, cfgBase, conf, trainDesde, testDesde)
                    If ops.Count < minOpsEntrenamiento Then Continue For
                    Dim pnl = ops.Sum(Function(o) o.ProfitLoss)
                    If pnl > mejorPnl Then
                        mejorPnl = pnl : mejorConf = conf : mejorOps = ops.Count
                        mejorWin = ops.Where(Function(o) o.Estado = "Ganado").Count() / CDbl(ops.Count)
                    End If
                Next

                ' 2) Aplicarlo a la prueba (si hubo umbral rentable)
                Dim testOps As New List(Of BacktestOperacion)()
                If mejorConf >= 0 Then
                    testOps = SimularRango(velas, estrategia, cfgBase, mejorConf, testDesde, testHasta)
                    opsPrueba.AddRange(testOps)
                End If
                Dim g = testOps.Where(Function(o) o.Estado = "Ganado").Count()
                ventanas.Add(New JObject From {
                    {"entrenamiento", Fecha(trainDesde) & " / " & Fecha(testDesde)},
                    {"prueba", Fecha(testDesde) & " / " & Fecha(testHasta)},
                    {"confianza_elegida", If(mejorConf >= 0, CType(mejorConf, JToken), "no opera")},
                    {"entrenamiento_ops", mejorOps},
                    {"entrenamiento_win_rate", Math.Round(mejorWin, 4)},
                    {"prueba_ops", testOps.Count},
                    {"prueba_win_rate", If(testOps.Count > 0, Math.Round(g / CDbl(testOps.Count), 4), 0.0)},
                    {"prueba_pnl_por_dolar", Math.Round(CDbl(testOps.Sum(Function(o) o.ProfitLoss)), 2)}
                })
                testDesde = testHasta
            Loop

            Dim n = opsPrueba.Count
            Dim gan = opsPrueba.Where(Function(o) o.Estado = "Ganado").Count()
            Dim ic = Estadistica.IntervaloWilson(gan, n)
            Dim resultado = New JObject From {
                {"ok", True},
                {"dias_entrenamiento", diasEntrenamiento}, {"dias_prueba", diasPrueba},
                {"grilla_confianza", New JArray(grilla)},
                {"ventanas", ventanas},
                {"total_prueba", New JObject From {
                    {"operaciones", n}, {"ganadas", gan},
                    {"win_rate", If(n > 0, Math.Round(gan / CDbl(n), 4), 0.0)},
                    {"ic95_inf", Math.Round(ic.Item1, 4)}, {"ic95_sup", Math.Round(ic.Item2, 4)},
                    {"pnl_por_dolar", Math.Round(CDbl(opsPrueba.Sum(Function(o) o.ProfitLoss)), 2)},
                    {"punto_equilibrio", Math.Round(Estadistica.PuntoEquilibrio(cfgBase.PayoutPct), 4)},
                    {"veredicto", Estadistica.Veredicto(gan, n, cfgBase.PayoutPct)}
                }}
            }
            ' Las ventanas de prueba anteriores al corte pueden caer dentro del periodo con el que
            ' se DISENARON las reglas (el walk-forward solo re-optimiza la confianza). Solo las
            ' operaciones posteriores al corte son totalmente limpias.
            If corteEpoch > 0 Then
                resultado("total_prueba_antes_del_corte") = Totales(opsPrueba.Where(Function(o) o.EpochEntrada < corteEpoch).ToList(), cfgBase.PayoutPct)
                resultado("total_prueba_despues_del_corte") = Totales(opsPrueba.Where(Function(o) o.EpochEntrada >= corteEpoch).ToList(), cfgBase.PayoutPct)
                resultado("corte_utc") = Fecha(corteEpoch)
            End If
            Return resultado
        End Function

        Private Function Totales(ops As List(Of BacktestOperacion), payout As Decimal) As JObject
            Dim n = ops.Count
            Dim g = ops.Where(Function(o) o.Estado = "Ganado").Count()
            Dim ic = Estadistica.IntervaloWilson(g, n)
            Return New JObject From {
                {"operaciones", n}, {"ganadas", g},
                {"win_rate", If(n > 0, Math.Round(g / CDbl(n), 4), 0.0)},
                {"ic95_inf", Math.Round(ic.Item1, 4)}, {"ic95_sup", Math.Round(ic.Item2, 4)},
                {"pnl_por_dolar", Math.Round(CDbl(ops.Sum(Function(o) o.ProfitLoss)), 2)},
                {"veredicto", Estadistica.Veredicto(g, n, payout)}
            }
        End Function

        ''' <summary>
        ''' Simula y devuelve solo las operaciones con entrada en [desde, hasta).
        ''' Incluye las velas previas necesarias para los indicadores y las posteriores
        ''' para que la ultima operacion pueda salir.
        ''' </summary>
        Friend Function SimularRango(velas As List(Of CandleModel), estrategia As IEstrategia, cfgBase As BacktestConfig,
                                     confianza As Integer, desde As Long, hasta As Long) As List(Of BacktestOperacion)
            Dim iDesde = velas.FindIndex(Function(v) v.Epoch >= desde)
            If iDesde < 0 Then Return New List(Of BacktestOperacion)()
            Dim iHasta = velas.FindLastIndex(Function(v) v.Epoch < hasta)
            If iHasta < iDesde Then Return New List(Of BacktestOperacion)()
            Dim ini = Math.Max(0, iDesde - cfgBase.VentanaIndicadores)
            Dim finIdx = Math.Min(velas.Count - 1, iHasta + cfgBase.DuracionVelas + 1)
            Dim tramo = velas.GetRange(ini, finIdx - ini + 1)
            Dim cfg = ClonarConfig(cfgBase)
            cfg.MinConfianza = confianza
            cfg.UnaPosicionALaVez = True
            If tramo.Count < cfg.VentanaIndicadores + cfg.DuracionVelas + 1 Then Return New List(Of BacktestOperacion)()
            Dim sim = BacktestMotor.Simular(tramo, estrategia, cfg)
            Return sim.Operaciones.Where(Function(o) o.EpochEntrada >= desde AndAlso o.EpochEntrada < hasta).ToList()
        End Function

        Friend Function ClonarConfig(c As BacktestConfig) As BacktestConfig
            Dim n As New BacktestConfig With {
                .Symbol = c.Symbol, .Timeframe = c.Timeframe, .Platform = c.Platform,
                .DesdeEpoch = c.DesdeEpoch, .HastaEpoch = c.HastaEpoch, .DuracionVelas = c.DuracionVelas,
                .PayoutPct = c.PayoutPct, .MinConfianza = c.MinConfianza, .VentanaIndicadores = c.VentanaIndicadores,
                .GuardarContextoVelas = 0, .UnaPosicionALaVez = c.UnaPosicionALaVez
            }
            If c.Cuentas.Count = 0 Then
                n.Cuentas.Add(New BacktestCuenta With {.NombreCuenta = "wf", .BalanceInicial = 1000D, .MontoPorOperacion = 1D})
            Else
                For Each cu In c.Cuentas
                    n.Cuentas.Add(New BacktestCuenta With {.NombreCuenta = cu.NombreCuenta, .BalanceInicial = cu.BalanceInicial,
                                                           .MontoPorOperacion = cu.MontoPorOperacion})
                Next
            End If
            Return n
        End Function

        Private Function Fecha(epoch As Long) As String
            Return DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime.ToString("yyyy-MM-dd")
        End Function

    End Module

End Namespace
