' ============================================================
' EstrategiaEmaRsi.vb
' Estrategia de trading: EMA(5) + EMA(10) + RSI(14)
'
' Lógica de señal:
'   Score = 0
'   +1/-1  si EMA5 > o < EMA10 (tendencia)
'   +2/-2  si RSI14 < 30 (sobreventa) o > 70 (sobrecompra)
'   +1/-1  por cada una de las últimas 3 velas alcista/bajista
'
'   score > 0 → CALL | score < 0 → PUT | score = 0 → NEUTRAL
'   confianza = |score| / 5 * 100  (máximo 5 puntos)
'
' Usa IndicatorRegistry: MovingAverages(EMA,5/10) y RSI(14)
' ============================================================
Imports System.Collections.Generic
Imports System.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Indicators

Namespace WpfDerivClientVB.Estrategias

    Public Class EstrategiaEmaRsi
        Implements IEstrategia

        Public ReadOnly Property Nombre As String Implements IEstrategia.Nombre
            Get
                Return "EmaRsi"
            End Get
        End Property

        Public ReadOnly Property Descripcion As String Implements IEstrategia.Descripcion
            Get
                Return "EMA(5) + EMA(10) + RSI(14). " &
                       "Cruces de medias para tendencia, RSI para zonas extremas " &
                       "y dirección de las últimas 3 velas. " &
                       "Score máx ±5. Confianza = |score|/5×100."
            End Get
        End Property

        Public Function Analizar(candles As IEnumerable(Of CandleModel)) As EstrategiaResultado Implements IEstrategia.Analizar
            Dim lista = candles.ToList()
            Dim resultado As New EstrategiaResultado()

            If lista.Count < 15 Then
                resultado.Motivo = "Insuficientes velas (mínimo 15)"
                Return resultado
            End If

            Dim last = lista.Count - 1

            ' Calcular indicadores via registry
            Dim ema5Res  = New MovingAverages(MovingAverageType.EMA, 5).Calculate(lista)
            Dim ema10Res = New MovingAverages(MovingAverageType.EMA, 10).Calculate(lista)
            Dim rsiRes   = New RSI(14).Calculate(lista)

            Dim ema5  = If(ema5Res(last).IndicatorValue.HasValue,  ema5Res(last).IndicatorValue.Value,  0D)
            Dim ema10 = If(ema10Res(last).IndicatorValue.HasValue, ema10Res(last).IndicatorValue.Value, 0D)
            Dim rsi14 = If(rsiRes(last).IndicatorValue.HasValue,   rsiRes(last).IndicatorValue.Value,   50D)

            ' Scoring
            Dim score As Integer = 0
            Dim motivos As New List(Of String)()

            If ema5 > ema10 Then
                score += 1
                motivos.Add("EMA5>EMA10 (alcista)")
            ElseIf ema5 < ema10 Then
                score -= 1
                motivos.Add("EMA5<EMA10 (bajista)")
            End If

            If rsi14 < 30 Then
                score += 2
                motivos.Add("RSI sobreventa")
            ElseIf rsi14 > 70 Then
                score -= 2
                motivos.Add("RSI sobrecompra")
            End If

            For j = Math.Max(0, last - 2) To last
                If lista(j).ClosePrice > lista(j).OpenPrice Then
                    score += 1
                ElseIf lista(j).ClosePrice < lista(j).OpenPrice Then
                    score -= 1
                End If
            Next

            ' Resultado
            resultado.Senal     = If(score > 0, "CALL", If(score < 0, "PUT", "NEUTRAL"))
            resultado.Confianza = CInt(Math.Min(100, Math.Round(Math.Abs(score) / 5.0 * 100, 0)))
            resultado.Motivo    = If(motivos.Count > 0, String.Join(" | ", motivos), "Sin señal clara")
            resultado.Datos("EMA5")  = ema5
            resultado.Datos("EMA10") = ema10
            resultado.Datos("RSI14") = rsi14
            resultado.Datos("Score") = score

            Return resultado
        End Function

    End Class

End Namespace
