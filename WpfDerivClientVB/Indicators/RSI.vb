Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Indicador de Momentum: Relative Strength Index (RSI).
    ''' Desarrollado por J. Welles Wilder (1978).
    '''
    ''' Mide la velocidad y magnitud de los cambios de precio en una escala 0-100.
    '''
    ''' Interpretación estándar:
    '''   - RSI > 70 → Sobrecompra (posible reversión bajista)
    '''   - RSI &lt; 30 → Sobreventa  (posible reversión alcista)
    '''   - RSI = 50 → Neutralidad
    '''
    ''' Algoritmo: Método de suavizado de Wilder (RMA - Running Moving Average)
    '''   AvgGain / AvgLoss iniciales = SMA del primer período
    '''   Siguientes = (Prev * (N-1) + Current) / N
    ''' </summary>
    Public Class RSI
        Implements IIndicator

        Private ReadOnly _period As Integer

        ''' <param name="period">Período de cálculo. Wilder recomienda 14.</param>
        Public Sub New(Optional period As Integer = 14)
            _period = period
        End Sub

        ''' <inheritdoc/>
        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("RSI({0})", _period)
            End Get
        End Property

        ''' <inheritdoc/>
        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return String.Format(
                    "Relative Strength Index de {0} períodos (Wilder, 1978). " &
                    "Oscilador de momentum en escala 0-100. " &
                    "Sobrecompra: RSI > 70. Sobreventa: RSI < 30. Neutral: RSI = 50. " &
                    "Útil para identificar divergencias precio-momentum.", _period)
            End Get
        End Property

        ''' <inheritdoc/>
        Public ReadOnly Property Category As String Implements IIndicator.Category
            Get
                Return "Momentum"
            End Get
        End Property

        ''' <summary>
        ''' Calcula el RSI para cada vela. Los primeros <c>period</c> resultados tienen
        ''' IndicatorValue = Nothing (se necesitan period+1 cierres para el primer valor).
        ''' </summary>
        Public Function Calculate(candles As IEnumerable(Of CandleModel)) As List(Of IndicatorResult) Implements IIndicator.Calculate
            Dim candleList As List(Of CandleModel) = candles.ToList()
            Dim results As New List(Of IndicatorResult)(candleList.Count)
            Dim labelStr As String = Me.Name

            Dim avgGain As Decimal = 0
            Dim avgLoss As Decimal = 0
            Dim initialized As Boolean = False

            Dim idx As Integer = 0
            Do While idx < candleList.Count
                Dim item As CandleModel = candleList(idx)
                Dim result As New IndicatorResult() With {
                    .Epoch = item.Epoch,
                    .OpenTime = item.OpenTime,
                    .Label = labelStr
                }

                If idx = 0 Then
                    ' Primera vela: sin cambio de precio anterior
                    result.IndicatorValue = Nothing

                ElseIf idx < _period Then
                    ' Acumulando datos: no hay suficientes para el primer RSI
                    result.IndicatorValue = Nothing

                ElseIf idx = _period Then
                    ' -------------------------------------------------------
                    ' Inicialización: SMA de las primeras N ganancias/pérdidas
                    ' -------------------------------------------------------
                    Dim sumGain As Decimal = 0
                    Dim sumLoss As Decimal = 0
                    Dim k As Integer = 1
                    Do While k <= _period
                        Dim change As Decimal = candleList(k).ClosePrice - candleList(k - 1).ClosePrice
                        If change > 0 Then
                            sumGain += change
                        Else
                            sumLoss += Math.Abs(change)
                        End If
                        k += 1
                    Loop
                    avgGain = sumGain / _period
                    avgLoss = sumLoss / _period
                    initialized = True
                    result.IndicatorValue = ComputeRSI(avgGain, avgLoss)

                Else
                    ' -------------------------------------------------------
                    ' Suavizado de Wilder: RMA = (Prev*(N-1) + Current) / N
                    ' -------------------------------------------------------
                    Dim change As Decimal = item.ClosePrice - candleList(idx - 1).ClosePrice
                    Dim gain As Decimal = If(change > 0, change, 0D)
                    Dim loss As Decimal = If(change < 0, Math.Abs(change), 0D)

                    avgGain = (avgGain * (_period - 1) + gain) / _period
                    avgLoss = (avgLoss * (_period - 1) + loss) / _period
                    result.IndicatorValue = ComputeRSI(avgGain, avgLoss)
                End If

                results.Add(result)
                idx += 1
            Loop

            Return results
        End Function

        ''' <summary>
        ''' Fórmula RSI: 100 - 100 / (1 + RS). Si avgLoss = 0, RSI = 100.
        ''' </summary>
        Private Shared Function ComputeRSI(avgGain As Decimal, avgLoss As Decimal) As Decimal
            If avgLoss = 0D Then Return 100D
            Dim rs As Decimal = avgGain / avgLoss
            Return 100D - (100D / (1D + rs))
        End Function
    End Class

End Namespace
