' ============================================================
' MultiplicadorMotor.vb
'
' Backtest de contratos con multiplicador de Deriv (MULTUP /
' MULTDOWN). A diferencia de Rise/Fall, el resultado no es "gano
' el payout o pierdo todo": se gana o pierde en proporcion al
' movimiento del precio, con take profit, stop loss y stop out.
' Asi una estrategia puede ganar acertando menos del 50% si sus
' ganancias son mayores que sus perdidas.
'
' Modelo (verificado con cotizaciones reales de Deriv):
'   nocional  = stake x multiplicador
'   comision  = nocional x comision_pct      (se paga al entrar)
'   pnl(p)    = nocional x dir x (p - entrada) / entrada - comision
'   stop out  : la perdida nunca supera el stake
'
' Supuestos conservadores:
'   - Entrada al cierre de la vela de la senal.
'   - Si en una misma vela se tocan el take profit y el stop loss,
'     cuenta el stop loss.
'   - Un hueco en contra se paga al precio de apertura (con tope
'     en el stake); un hueco a favor solo cobra el take profit.
'   - Si faltan velas mientras la posicion esta abierta, la
'     operacion se descarta (no se puede saber que paso).
'   - Una sola posicion a la vez.
' ============================================================
Imports System.Collections.Generic
Imports System.Linq
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Estrategias

Namespace WpfDerivClientVB.Backtesting

    Public Class MultiplicadorConfig
        Public Property Multiplicador As Integer = 100
        ''' <summary>Monto arriesgado (USD). Los resultados son proporcionales: con 1 se leen "por dolar".</summary>
        Public Property Stake As Decimal = 1D
        ''' <summary>Take profit en USD (0 = sin take profit).</summary>
        Public Property TakeProfit As Decimal = 0D
        ''' <summary>Stop loss en USD (0 = solo stop out, que es perder el stake).</summary>
        Public Property StopLoss As Decimal = 0D
        ''' <summary>Cierre por tiempo tras N velas si no salto ningun limite.</summary>
        Public Property MaxVelas As Integer = 6
        ''' <summary>Comision como fraccion del nocional (R_100 medido: 0.00055).</summary>
        Public Property ComisionPct As Double = 0.0
    End Class

    Public Class OperacionMultiplicador
        Public Property IndiceEntrada As Integer
        Public Property EpochEntrada As Long
        Public Property Tipo As String              ' CALL (MULTUP) | PUT (MULTDOWN)
        Public Property PrecioEntrada As Decimal
        Public Property EpochSalida As Long
        Public Property PrecioSalida As Decimal
        Public Property Motivo As String            ' TP | SL | StopOut | Tiempo
        Public Property Pnl As Decimal
        Public Property VelasEnPosicion As Integer
    End Class

    Public Class SenalMultiplicador
        Public Property Indice As Integer
        Public Property Tipo As String
    End Class

    Public Module MultiplicadorMotor

        ''' <summary>
        ''' Senales de la estrategia (una pasada sobre todas las velas). Se calculan una vez y
        ''' se reutilizan para cada combinacion de multiplicador / limites.
        ''' </summary>
        Public Function CalcularSenales(velas As List(Of CandleModel), estrategia As IEstrategia,
                                        ventana As Integer, minConfianza As Integer) As List(Of SenalMultiplicador)
            Dim res As New List(Of SenalMultiplicador)()
            For i = ventana - 1 To velas.Count - 2
                Dim r = estrategia.Analizar(velas.GetRange(i - ventana + 1, ventana))
                If r Is Nothing OrElse r.Senal = "NEUTRAL" OrElse r.Confianza < minConfianza Then Continue For
                If r.Senal <> "CALL" AndAlso r.Senal <> "PUT" Then Continue For
                res.Add(New SenalMultiplicador With {.Indice = i, .Tipo = r.Senal})
            Next
            Return res
        End Function

        ''' <summary>
        ''' Simula las senales como multiplicadores. tfSeg = segundos por vela (para detectar huecos;
        ''' 0 = no comprobar). descartadas = operaciones que cruzaban un hueco de datos.
        ''' </summary>
        Public Function Simular(velas As List(Of CandleModel), senales As IEnumerable(Of SenalMultiplicador),
                                cfg As MultiplicadorConfig, tfSeg As Long,
                                Optional ByRef descartadas As Integer = 0) As List(Of OperacionMultiplicador)
            Dim ops As New List(Of OperacionMultiplicador)()
            descartadas = 0
            Dim libreDesde As Integer = -1
            Dim S = CDbl(cfg.Stake)
            Dim nocional = S * cfg.Multiplicador
            Dim comision = nocional * cfg.ComisionPct
            Dim perdidaMax = If(cfg.StopLoss > 0D, Math.Min(CDbl(cfg.StopLoss), S), S)
            Dim motivoPerdida = If(cfg.StopLoss > 0D AndAlso CDbl(cfg.StopLoss) < S, "SL", "StopOut")
            Dim tp = CDbl(cfg.TakeProfit)
            Dim maxVelas = Math.Max(1, cfg.MaxVelas)

            For Each sn In senales.OrderBy(Function(x) x.Indice)
                Dim i = sn.Indice
                If i < libreDesde Then Continue For
                If i + 1 >= velas.Count Then Exit For
                Dim dir As Double = If(sn.Tipo = "CALL", 1.0, -1.0)
                Dim entrada = CDbl(velas(i).ClosePrice)
                If entrada <= 0 Then Continue For
                Dim pnlEn = Function(p As Double) nocional * dir * (p - entrada) / entrada - comision

                Dim op As New OperacionMultiplicador With {
                    .IndiceEntrada = i, .EpochEntrada = velas(i).Epoch, .Tipo = sn.Tipo, .PrecioEntrada = velas(i).ClosePrice
                }
                Dim salida As Integer = -1
                Dim pnl As Double = 0
                Dim hueco As Boolean = False

                If comision >= perdidaMax Then
                    ' La comision ya consume el limite de perdida: se cierra en el acto
                    salida = i : pnl = -perdidaMax : op.Motivo = motivoPerdida
                Else
                    ' Precios donde saltan los limites
                    Dim precioPerdida = entrada * (1 + dir * (comision - perdidaMax) / nocional)
                    Dim precioTp = If(tp > 0, entrada * (1 + dir * (tp + comision) / nocional), Double.NaN)
                    Dim ultima = Math.Min(velas.Count - 1, i + maxVelas)
                    If ultima < i + maxVelas Then Exit For   ' no hay velas suficientes al final
                    For j = i + 1 To ultima
                        If tfSeg > 0 AndAlso velas(j).Epoch - velas(j - 1).Epoch <> tfSeg Then
                            hueco = True
                            Exit For
                        End If
                        Dim v = velas(j)
                        Dim abre = CDbl(v.OpenPrice)
                        Dim enContra = If(dir > 0, CDbl(v.Low), CDbl(v.High))
                        Dim aFavor = If(dir > 0, CDbl(v.High), CDbl(v.Low))
                        ' Hueco en contra al abrir
                        If dir * (abre - precioPerdida) <= 0 Then
                            salida = j : pnl = Math.Max(-S, pnlEn(abre)) : op.Motivo = motivoPerdida : Exit For
                        End If
                        ' Toca el limite de perdida (si tambien toca el TP, cuenta la perdida)
                        If dir * (enContra - precioPerdida) <= 0 Then
                            salida = j : pnl = -perdidaMax : op.Motivo = motivoPerdida : Exit For
                        End If
                        If tp > 0 AndAlso dir * (aFavor - precioTp) >= 0 Then
                            salida = j : pnl = tp : op.Motivo = "TP" : Exit For
                        End If
                        If j = ultima Then
                            salida = j : pnl = Math.Max(-S, pnlEn(CDbl(v.ClosePrice))) : op.Motivo = "Tiempo"
                        End If
                    Next
                End If

                If hueco OrElse salida < 0 Then
                    descartadas += 1
                    Continue For
                End If
                op.EpochSalida = velas(salida).Epoch
                op.PrecioSalida = velas(salida).ClosePrice
                op.Pnl = CDec(Math.Round(pnl, 6))
                op.VelasEnPosicion = salida - i
                ops.Add(op)
                libreDesde = salida
            Next
            Return ops
        End Function

        ''' <summary>
        ''' Resumen de un conjunto de operaciones (en USD con el stake dado). La ventaja se mide con
        ''' el resultado medio por operacion: hay ventaja si el limite inferior de su IC95 es mayor que 0.
        ''' </summary>
        Public Function Resumen(ops As List(Of OperacionMultiplicador)) As JObject
            Dim n = ops.Count
            Dim pnls = ops.Select(Function(o) CDbl(o.Pnl)).ToList()
            Dim ganadas = pnls.Where(Function(p) p > 0).ToList()
            Dim perdidas = pnls.Where(Function(p) p <= 0).ToList()
            Dim media = If(n > 0, pnls.Average(), 0.0)
            Dim desv = If(n > 1, Math.Sqrt(pnls.Sum(Function(p) (p - media) * (p - media)) / (n - 1)), 0.0)
            Dim margen = If(n > 1, 1.96 * desv / Math.Sqrt(n), 0.0)
            Dim sumaG = ganadas.Sum(), sumaP = -perdidas.Sum()
            Return New JObject From {
                {"operaciones", n},
                {"win_rate", If(n > 0, Math.Round(ganadas.Count / CDbl(n), 4), 0.0)},
                {"pnl_total", Math.Round(pnls.Sum(), 2)},
                {"pnl_medio", Math.Round(media, 4)},
                {"ic95_inf", Math.Round(media - margen, 4)},
                {"ic95_sup", Math.Round(media + margen, 4)},
                {"ganancia_media", If(ganadas.Count > 0, Math.Round(ganadas.Average(), 4), 0.0)},
                {"perdida_media", If(perdidas.Count > 0, Math.Round(perdidas.Average(), 4), 0.0)},
                {"profit_factor", If(sumaP > 0, Math.Round(sumaG / sumaP, 3), If(sumaG > 0, 99.0, 0.0))},
                {"caida_maxima", Math.Round(MonteCarlo.CaidaMaxima(pnls.ToArray()), 2)},
                {"velas_medias", If(n > 0, Math.Round(ops.Average(Function(o) o.VelasEnPosicion), 1), 0.0)},
                {"salidas", New JObject From {
                    {"TP", ops.Where(Function(o) o.Motivo = "TP").Count()},
                    {"SL", ops.Where(Function(o) o.Motivo = "SL").Count()},
                    {"StopOut", ops.Where(Function(o) o.Motivo = "StopOut").Count()},
                    {"Tiempo", ops.Where(Function(o) o.Motivo = "Tiempo").Count()}
                }},
                {"veredicto", Veredicto(n, media, margen)}
            }
        End Function

        Friend Function Veredicto(n As Integer, media As Double, margen As Double) As String
            If n < 30 Then Return "MUESTRA INSUFICIENTE"
            If media - margen > 0 Then Return "VENTAJA ESTADISTICA"
            If media > 0 Then Return "POSITIVO, NO SIGNIFICATIVO"
            Return "SIN VENTAJA"
        End Function

    End Module

End Namespace
