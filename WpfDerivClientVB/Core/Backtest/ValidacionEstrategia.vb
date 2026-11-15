' ============================================================
' ValidacionEstrategia.vb
'
' Valida una estrategia FUERA DE MUESTRA: separa el historico en
' el periodo con el que se diseno (antes de la fecha de corte) y
' un periodo ciego (despues), y para cada uno calcula:
'   - acierto e intervalo de confianza de Wilson al 95%,
'   - punto de equilibrio segun el payout (100 / (100 + payout)),
'   - ganancia por cada $1 apostado,
'   - un veredicto honesto.
' Usa el motor corregido (salida por tiempo, descarte por huecos)
' con UnaPosicionALaVez = True (resultados independientes).
' No guarda nada en BD.
' ============================================================
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Estrategias

Namespace WpfDerivClientVB.Backtesting

    Public Module Estadistica

        ''' <summary>Intervalo de confianza de Wilson para una proporcion (z=1.96 -> 95%).</summary>
        Public Function IntervaloWilson(exitos As Integer, n As Integer, Optional z As Double = 1.96) As Tuple(Of Double, Double)
            If n <= 0 Then Return Tuple.Create(0.0, 0.0)
            Dim p = exitos / CDbl(n)
            Dim z2 = z * z
            Dim centro = (p + z2 / (2 * n)) / (1 + z2 / n)
            Dim margen = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / (1 + z2 / n)
            Return Tuple.Create(Math.Max(0.0, centro - margen), Math.Min(1.0, centro + margen))
        End Function

        ''' <summary>Acierto minimo para no perder con un payout dado (85 -> 0.5405).</summary>
        Public Function PuntoEquilibrio(payoutPct As Decimal) As Double
            If payoutPct <= 0D Then Return 1.0
            Return 100.0 / (100.0 + CDbl(payoutPct))
        End Function

        ''' <summary>
        ''' Veredicto: hace falta muestra suficiente y que el limite INFERIOR del intervalo
        ''' supere el punto de equilibrio para hablar de ventaja.
        ''' </summary>
        Public Function Veredicto(ganadas As Integer, n As Integer, payoutPct As Decimal, Optional minimoOps As Integer = 30) As String
            If n < minimoOps Then Return "MUESTRA INSUFICIENTE"
            Dim ic = IntervaloWilson(ganadas, n)
            Dim be = PuntoEquilibrio(payoutPct)
            If ic.Item1 > be Then Return "VENTAJA ESTADISTICA"
            If ganadas / CDbl(n) > be Then Return "POSITIVO, NO SIGNIFICATIVO"
            Return "SIN VENTAJA"
        End Function

        ''' <summary>
        ''' Veredicto corregido por pruebas multiples (Bonferroni): si se han probado N hipotesis,
        ''' cada una debe superar el minimo con un intervalo mas exigente (nivel 95% repartido
        ''' entre las N). Con N = 1 coincide con Veredicto.
        ''' </summary>
        Public Function VeredictoCorregido(ganadas As Integer, n As Integer, payoutPct As Decimal, numeroPruebas As Integer,
                                           Optional minimoOps As Integer = 30) As String
            If n < minimoOps Then Return "MUESTRA INSUFICIENTE"
            Dim z = ZDeCola(0.025 / Math.Max(1, numeroPruebas))
            Dim ic = IntervaloWilson(ganadas, n, z)
            Dim be = PuntoEquilibrio(payoutPct)
            If ic.Item1 > be Then Return "VENTAJA ESTADISTICA"
            If ganadas / CDbl(n) > be Then Return "POSITIVO, NO SIGNIFICATIVO"
            Return "SIN VENTAJA"
        End Function

        ''' <summary>
        ''' z tal que P(Z &gt; z) = alfa en la normal estandar (inversa de Acklam, error &lt; 1e-9).
        ''' alfa = 0.025 -> 1.96.
        ''' </summary>
        Public Function ZDeCola(alfa As Double) As Double
            Dim p = 1.0 - Math.Min(Math.Max(alfa, 1.0E-15), 0.5)
            Dim a() As Double = {-39.69683028665376, 220.9460984245205, -275.9285104469687, 138.357751867269, -30.66479806614716, 2.506628277459239}
            Dim b() As Double = {-54.47609879822406, 161.5858368580409, -155.6989798598866, 66.80131188771972, -13.28068155288572}
            Dim c() As Double = {-0.007784894002430293, -0.3223964580411365, -2.400758277161838, -2.549732539343734, 4.374664141464968, 2.938163982698783}
            Dim d() As Double = {0.007784695709041462, 0.3224671290700398, 2.445134137142996, 3.754408661907416}
            Const pAlto As Double = 1 - 0.02425
            If p > pAlto Then
                Dim q = Math.Sqrt(-2 * Math.Log(1 - p))
                Return -(((((c(0) * q + c(1)) * q + c(2)) * q + c(3)) * q + c(4)) * q + c(5)) /
                        ((((d(0) * q + d(1)) * q + d(2)) * q + d(3)) * q + 1)
            End If
            Dim r = p - 0.5
            Dim t = r * r
            Return (((((a(0) * t + a(1)) * t + a(2)) * t + a(3)) * t + a(4)) * t + a(5)) * r /
                   (((((b(0) * t + b(1)) * t + b(2)) * t + b(3)) * t + b(4)) * t + 1)
        End Function
    End Module

    Public Module ValidacionEstrategia

        Public Async Function ValidarAsync(estrategia As IEstrategia, symbol As String, timeframe As String,
                                           duracionVelas As Integer, minConfianza As Integer, payoutPct As Decimal,
                                           corteEpoch As Long,
                                           Optional desdeEpoch As Long = 0,
                                           Optional hastaEpoch As Long = 0,
                                           Optional fuente As String = FuentesVelas.DERIV) As Task(Of JObject)
            If hastaEpoch <= 0 Then hastaEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            Dim repo = New CandleRepository(ConfigManager.LoadDbConfig().GetConnectionString())
            fuente = FuentesVelas.Normalizar(fuente)
            Dim velas = Await repo.GetCandlesRangoAsync(fuente, symbol, timeframe, desdeEpoch, hastaEpoch)
            Dim r = Validar(velas, estrategia, symbol, timeframe, duracionVelas, minConfianza, payoutPct, corteEpoch)
            r("fuente") = fuente
            Return r
        End Function

        ''' <summary>Validacion sobre velas ya cargadas (pura: se puede probar).</summary>
        Public Function Validar(velas As List(Of CandleModel), estrategia As IEstrategia, symbol As String,
                                timeframe As String, duracionVelas As Integer, minConfianza As Integer,
                                payoutPct As Decimal, corteEpoch As Long) As JObject
            Dim cfg As New BacktestConfig With {
                .Symbol = symbol, .Timeframe = timeframe, .DuracionVelas = duracionVelas,
                .PayoutPct = payoutPct, .MinConfianza = minConfianza, .VentanaIndicadores = 100,
                .GuardarContextoVelas = 0, .UnaPosicionALaVez = True
            }
            cfg.Cuentas.Add(New BacktestCuenta With {.NombreCuenta = "validacion", .BalanceInicial = 1000D, .MontoPorOperacion = 1D})

            If velas.Count < cfg.VentanaIndicadores + duracionVelas + 1 Then
                Return New JObject From {{"ok", False}, {"error", String.Format("Velas insuficientes ({0}) para {1} {2}.", velas.Count, symbol, timeframe)}}
            End If

            ' Una sola simulacion continua; luego se separa por fecha de entrada
            Dim sim = BacktestMotor.Simular(velas, estrategia, cfg)
            Dim ops = sim.Operaciones
            Dim enDiseno = ops.Where(Function(o) o.EpochEntrada < corteEpoch).ToList()
            Dim ciego = ops.Where(Function(o) o.EpochEntrada >= corteEpoch).ToList()
            Dim velasCiego = velas.Where(Function(v) v.Epoch >= corteEpoch).ToList()

            Return New JObject From {
                {"ok", True},
                {"estrategia", estrategia.Nombre},
                {"symbol", symbol}, {"timeframe", timeframe},
                {"duracion_velas", duracionVelas}, {"min_confianza", minConfianza},
                {"payout_pct", payoutPct},
                {"punto_equilibrio", Math.Round(Estadistica.PuntoEquilibrio(payoutPct), 4)},
                {"corte_utc", DateTimeOffset.FromUnixTimeSeconds(corteEpoch).UtcDateTime.ToString("yyyy-MM-dd")},
                {"descartadas_por_hueco", sim.DescartadasPorHueco},
                {"periodo_diseno", Resumen(enDiseno, payoutPct, velas.FirstOrDefault()?.Epoch, corteEpoch)},
                {"periodo_ciego", Resumen(ciego, payoutPct, corteEpoch, velas.LastOrDefault()?.Epoch)},
                {"velas_periodo_ciego", velasCiego.Count}
            }
        End Function

        Private Function Resumen(ops As List(Of BacktestOperacion), payoutPct As Decimal,
                                 desde As Long?, hasta As Long?) As JObject
            Dim n = ops.Count
            Dim g = ops.Where(Function(o) o.Estado = "Ganado").Count()
            Dim ic = Estadistica.IntervaloWilson(g, n)
            Dim f = Function(e As Long?) If(e.HasValue AndAlso e.Value > 0,
                                             DateTimeOffset.FromUnixTimeSeconds(e.Value).UtcDateTime.ToString("yyyy-MM-dd"), "")
            Return New JObject From {
                {"desde", f(desde)}, {"hasta", f(hasta)},
                {"operaciones", n}, {"ganadas", g},
                {"win_rate", If(n > 0, Math.Round(g / CDbl(n), 4), 0.0)},
                {"ic95_inf", Math.Round(ic.Item1, 4)}, {"ic95_sup", Math.Round(ic.Item2, 4)},
                {"pnl_por_dolar", Math.Round(CDbl(ops.Sum(Function(o) o.ProfitLoss)), 2)},
                {"veredicto", Estadistica.Veredicto(g, n, payoutPct)}
            }
        End Function

    End Module

End Namespace
