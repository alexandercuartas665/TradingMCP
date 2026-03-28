Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Tipos de media móvil disponibles.
    ''' </summary>
    Public Enum MovingAverageType
        ''' <summary>Simple Moving Average — promedio aritmético de los N cierres.</summary>
        SMA
        ''' <summary>Exponential Moving Average — mayor peso en datos recientes.</summary>
        EMA
        ''' <summary>Weighted Moving Average — peso lineal decreciente hacia el pasado.</summary>
        WMA
    End Enum

    ''' <summary>
    ''' Indicador de Medias Móviles: SMA, EMA y WMA.
    ''' Categoría: Trend (siguimiento de tendencia).
    '''
    ''' Todos los métodos reciben velas ordenadas oldest→newest y retornan
    ''' una entrada por vela. Los primeros (period-1) resultados tienen IndicatorValue = Nothing.
    '''
    ''' Uso directo (sin instanciar):
    '''   MovingAverages.SMA(candles, 20)
    '''   MovingAverages.EMA(candles, 14)
    '''   MovingAverages.WMA(candles, 10)
    '''
    ''' Uso como IIndicator (polimórfico):
    '''   Dim ma As IIndicator = New MovingAverages(MovingAverageType.EMA, 20)
    '''   Dim results = ma.Calculate(candles)
    ''' </summary>
    Public Class MovingAverages
        Implements IIndicator

        Private ReadOnly _period As Integer
        Private ReadOnly _maType As MovingAverageType

        ''' <param name="maType">Tipo de media: SMA, EMA o WMA.</param>
        ''' <param name="period">Número de períodos. Mínimo recomendado: 5.</param>
        Public Sub New(maType As MovingAverageType, period As Integer)
            _maType = maType
            _period = period
        End Sub

        ''' <inheritdoc/>
        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("{0}({1})", _maType.ToString(), _period)
            End Get
        End Property

        ''' <inheritdoc/>
        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Select Case _maType
                    Case MovingAverageType.SMA
                        Return String.Format("Simple Moving Average de {0} períodos. " &
                            "Promedio aritmético de los últimos {0} cierres. " &
                            "Señal alcista: precio cruza hacia arriba. Bajista: cruza hacia abajo.", _period)
                    Case MovingAverageType.EMA
                        Return String.Format("Exponential Moving Average de {0} períodos. " &
                            "Mayor peso en datos recientes (multiplicador k=2/(N+1)). " &
                            "Más reactiva que SMA. Útil para señales de cruce.", _period)
                    Case MovingAverageType.WMA
                        Return String.Format("Weighted Moving Average de {0} períodos. " &
                            "Peso lineal: la vela más reciente pesa {0}, la anterior {1}, etc. " &
                            "Intermedia entre SMA y EMA en reactividad.", _period, _period - 1)
                    Case Else
                        Return "Media Móvil"
                End Select
            End Get
        End Property

        ''' <inheritdoc/>
        Public ReadOnly Property Category As String Implements IIndicator.Category
            Get
                Return "Trend"
            End Get
        End Property

        ''' <inheritdoc/>
        Public Function Calculate(candles As IEnumerable(Of CandleModel)) As List(Of IndicatorResult) Implements IIndicator.Calculate
            Select Case _maType
                Case MovingAverageType.SMA : Return SMA(candles, _period)
                Case MovingAverageType.EMA : Return EMA(candles, _period)
                Case MovingAverageType.WMA : Return WMA(candles, _period)
                Case Else : Return New List(Of IndicatorResult)()
            End Select
        End Function

        ' ============================================================
        '  SMA — Simple Moving Average
        '  Promedio aritmético de los últimos N cierres.
        '  Complejidad temporal: O(n*period). Para cálculo en streaming
        '  considerar una ventana deslizante O(n).
        ' ============================================================

        ''' <summary>
        ''' Calcula la SMA para cada vela de la serie.
        ''' Los primeros (period-1) resultados tienen IndicatorValue = Nothing.
        ''' </summary>
        Public Shared Function SMA(candles As IEnumerable(Of CandleModel), period As Integer) As List(Of IndicatorResult)
            Dim candleList As List(Of CandleModel) = candles.ToList()
            Dim results As New List(Of IndicatorResult)(candleList.Count)
            Dim labelStr As String = String.Format("SMA({0})", period)

            Dim idx As Integer = 0
            Do While idx < candleList.Count
                Dim item As CandleModel = candleList(idx)
                Dim result As New IndicatorResult() With {
                    .Epoch = item.Epoch,
                    .OpenTime = item.OpenTime,
                    .Label = labelStr
                }

                If idx < period - 1 Then
                    result.IndicatorValue = Nothing
                Else
                    Dim total As Decimal = 0
                    Dim k As Integer = idx - period + 1
                    Do While k <= idx
                        total += candleList(k).ClosePrice
                        k += 1
                    Loop
                    result.IndicatorValue = total / period
                End If

                results.Add(result)
                idx += 1
            Loop
            Return results
        End Function

        ' ============================================================
        '  EMA — Exponential Moving Average
        '  Multiplicador k = 2/(period+1). El primer valor se inicializa
        '  como SMA de los primeros N períodos (método estándar).
        '  EMA(i) = Close(i)*k + EMA(i-1)*(1-k)
        ' ============================================================

        ''' <summary>
        ''' Calcula la EMA para cada vela de la serie.
        ''' Los primeros (period-1) resultados tienen IndicatorValue = Nothing.
        ''' </summary>
        Public Shared Function EMA(candles As IEnumerable(Of CandleModel), period As Integer) As List(Of IndicatorResult)
            Dim candleList As List(Of CandleModel) = candles.ToList()
            Dim results As New List(Of IndicatorResult)(candleList.Count)
            Dim labelStr As String = String.Format("EMA({0})", period)
            Dim multiplier As Decimal = 2D / (period + 1)
            Dim prevEma As Nullable(Of Decimal) = Nothing

            Dim idx As Integer = 0
            Do While idx < candleList.Count
                Dim item As CandleModel = candleList(idx)
                Dim result As New IndicatorResult() With {
                    .Epoch = item.Epoch,
                    .OpenTime = item.OpenTime,
                    .Label = labelStr
                }

                If idx < period - 1 Then
                    result.IndicatorValue = Nothing
                ElseIf idx = period - 1 Then
                    Dim total As Decimal = 0
                    Dim k As Integer = 0
                    Do While k < period
                        total += candleList(k).ClosePrice
                        k += 1
                    Loop
                    prevEma = total / period
                    result.IndicatorValue = prevEma
                Else
                    Dim currentEma As Decimal = (item.ClosePrice * multiplier) + (prevEma.Value * (1D - multiplier))
                    prevEma = currentEma
                    result.IndicatorValue = currentEma
                End If

                results.Add(result)
                idx += 1
            Loop
            Return results
        End Function

        ' ============================================================
        '  WMA — Weighted Moving Average
        '  Peso lineal: vela más reciente = N, siguiente = N-1, ... = 1
        '  Denominador = N*(N+1)/2
        ' ============================================================

        ''' <summary>
        ''' Calcula la WMA para cada vela de la serie.
        ''' Los primeros (period-1) resultados tienen IndicatorValue = Nothing.
        ''' </summary>
        Public Shared Function WMA(candles As IEnumerable(Of CandleModel), period As Integer) As List(Of IndicatorResult)
            Dim candleList As List(Of CandleModel) = candles.ToList()
            Dim results As New List(Of IndicatorResult)(candleList.Count)
            Dim labelStr As String = String.Format("WMA({0})", period)
            Dim weightSum As Decimal = CDec(period * (period + 1)) / 2D

            Dim idx As Integer = 0
            Do While idx < candleList.Count
                Dim item As CandleModel = candleList(idx)
                Dim result As New IndicatorResult() With {
                    .Epoch = item.Epoch,
                    .OpenTime = item.OpenTime,
                    .Label = labelStr
                }

                If idx < period - 1 Then
                    result.IndicatorValue = Nothing
                Else
                    Dim total As Decimal = 0
                    Dim peso As Integer = 1
                    Dim k As Integer = idx - period + 1
                    Do While k <= idx
                        total += candleList(k).ClosePrice * peso
                        peso += 1
                        k += 1
                    Loop
                    result.IndicatorValue = total / weightSum
                End If

                results.Add(result)
                idx += 1
            Loop
            Return results
        End Function
    End Class

End Namespace
