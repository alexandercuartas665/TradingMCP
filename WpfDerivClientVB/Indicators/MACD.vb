Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Resultado extendido de MACD con las tres líneas del indicador.
    ''' Hereda de <see cref="IndicatorResult"/> para compatibilidad con IIndicator.
    ''' El campo <see cref="IndicatorResult.IndicatorValue"/> almacena la línea MACD.
    ''' Los valores adicionales se exponen en AdditionalValues para serialización MCP:
    '''   "Signal"    → línea de señal (EMA de la línea MACD)
    '''   "Histogram" → diferencia MACD - Signal
    ''' </summary>
    Public Class MacdResult
        Inherits IndicatorResult

        ''' <summary>
        ''' Línea MACD = EMA(fastPeriod) - EMA(slowPeriod).
        ''' Alias de IndicatorValue para acceso semántico.
        ''' </summary>
        Public Property MacdLine As Nullable(Of Decimal)
            Get
                Return IndicatorValue
            End Get
            Set(v As Nullable(Of Decimal))
                IndicatorValue = v
            End Set
        End Property

        ''' <summary>
        ''' Línea de señal = EMA(signalPeriod) sobre la línea MACD.
        ''' Positivo: MACD por encima de Signal → impulso alcista.
        ''' </summary>
        Public Property SignalLine As Nullable(Of Decimal)

        ''' <summary>
        ''' Histograma = MACDLine - SignalLine.
        ''' Positivo y creciente → momentum alcista acelerando.
        ''' Negativo y decreciendo → momentum bajista acelerando.
        ''' </summary>
        Public Property Histogram As Nullable(Of Decimal)
    End Class

    ''' <summary>
    ''' Indicador de Tendencia: MACD (Moving Average Convergence Divergence).
    ''' Desarrollado por Gerald Appel (~1979).
    '''
    ''' Combina dos EMAs de diferente período para identificar cambios de tendencia
    ''' y evaluar la fuerza del momentum.
    '''
    ''' Componentes:
    '''   - Línea MACD   = EMA(fastPeriod) - EMA(slowPeriod)  [default: EMA12 - EMA26]
    '''   - Línea Señal  = EMA(signalPeriod) sobre la línea MACD [default: EMA9]
    '''   - Histograma   = MACDLine - SignalLine
    '''
    ''' Señales de trading clásicas:
    '''   - MACD cruza hacia arriba de Signal → compra
    '''   - MACD cruza hacia abajo de Signal → venta
    '''   - Divergencia precio/MACD → posible reversión de tendencia
    '''
    ''' Período mínimo de datos para primer valor válido:
    '''   slowPeriod + signalPeriod = 26 + 9 - 1 = 34 velas (parámetros por defecto)
    ''' </summary>
    Public Class MACD
        Implements IIndicator

        Private ReadOnly _fastPeriod As Integer
        Private ReadOnly _slowPeriod As Integer
        Private ReadOnly _signalPeriod As Integer

        ''' <param name="fastPeriod">Período EMA rápida. Estándar: 12.</param>
        ''' <param name="slowPeriod">Período EMA lenta. Estándar: 26.</param>
        ''' <param name="signalPeriod">Período de suavizado de señal. Estándar: 9.</param>
        Public Sub New(Optional fastPeriod As Integer = 12,
                       Optional slowPeriod As Integer = 26,
                       Optional signalPeriod As Integer = 9)
            _fastPeriod = fastPeriod
            _slowPeriod = slowPeriod
            _signalPeriod = signalPeriod
        End Sub

        ''' <inheritdoc/>
        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("MACD({0},{1},{2})", _fastPeriod, _slowPeriod, _signalPeriod)
            End Get
        End Property

        ''' <inheritdoc/>
        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return String.Format(
                    "MACD ({0},{1},{2}) — Indicador de tendencia y momentum. " &
                    "MACDLine = EMA({0}) - EMA({1}). " &
                    "SignalLine = EMA({2}) sobre MACDLine. " &
                    "Histograma = MACD - Signal. " &
                    "Cruce MACD↑Signal: señal alcista. Cruce MACD↓Signal: señal bajista. " &
                    "Primer valor válido en vela {3}.",
                    _fastPeriod, _slowPeriod, _signalPeriod, _slowPeriod + _signalPeriod - 1)
            End Get
        End Property

        ''' <inheritdoc/>
        Public ReadOnly Property Category As String Implements IIndicator.Category
            Get
                Return "Trend"
            End Get
        End Property

        ''' <summary>
        ''' Implementación IIndicator. Cada elemento es un <see cref="MacdResult"/>.
        ''' Para tipado fuerte use <see cref="CalculateMacd"/>.
        ''' </summary>
        Public Function Calculate(candles As IEnumerable(Of CandleModel)) As List(Of IndicatorResult) Implements IIndicator.Calculate
            Return CalculateMacd(candles).Cast(Of IndicatorResult).ToList()
        End Function

        ''' <summary>
        ''' Calcula el MACD completo con acceso tipado a MacdLine, SignalLine e Histogram.
        ''' Los primeros (slowPeriod + signalPeriod - 2) resultados tienen todos los campos = Nothing.
        ''' </summary>
        Public Function CalculateMacd(candles As IEnumerable(Of CandleModel)) As List(Of MacdResult)
            Dim candleList As List(Of CandleModel) = candles.ToList()
            Dim count As Integer = candleList.Count
            Dim labelStr As String = Me.Name

            ' ---- Paso 1: Calcular EMA rápida y lenta sobre todos los cierres ----
            Dim emaFast() As Decimal = CalcEMA(candleList, _fastPeriod)
            Dim emaSlow() As Decimal = CalcEMA(candleList, _slowPeriod)

            ' ---- Paso 2: Calcular línea MACD donde ambas EMAs son válidas ----
            '     Primera EMA válida:  índice period-1 (0-based)
            '     Primera MACD válida: índice slowPeriod-1
            Dim macdLine(count - 1) As Nullable(Of Decimal)
            Dim firstMacdIdx As Integer = _slowPeriod - 1

            Dim i As Integer = firstMacdIdx
            Do While i < count
                macdLine(i) = emaFast(i) - emaSlow(i)
                i += 1
            Loop

            ' ---- Paso 3: Calcular EMA de signalPeriod sobre la línea MACD ----
            '     Solo sobre los valores válidos (desde firstMacdIdx)
            Dim signalLine(count - 1) As Nullable(Of Decimal)
            Dim firstSignalIdx As Integer = firstMacdIdx + _signalPeriod - 1

            If firstSignalIdx < count Then
                ' EMA inicial de Signal = SMA de los primeros signalPeriod valores MACD
                Dim signalMultiplier As Decimal = 2D / (_signalPeriod + 1)
                Dim sumInit As Decimal = 0
                Dim k As Integer = firstMacdIdx
                Dim kEnd As Integer = firstMacdIdx + _signalPeriod - 1
                Do While k <= kEnd
                    sumInit += macdLine(k).Value
                    k += 1
                Loop
                signalLine(firstSignalIdx) = sumInit / _signalPeriod

                ' EMA de Signal para el resto
                i = firstSignalIdx + 1
                Do While i < count
                    signalLine(i) = (macdLine(i).Value * signalMultiplier) +
                                    (signalLine(i - 1).Value * (1D - signalMultiplier))
                    i += 1
                Loop
            End If

            ' ---- Paso 4: Construir resultados ----
            Dim results As New List(Of MacdResult)(count)
            i = 0
            Do While i < count
                Dim item As CandleModel = candleList(i)
                Dim result As New MacdResult() With {
                    .Epoch = item.Epoch,
                    .OpenTime = item.OpenTime,
                    .Label = labelStr
                }

                result.MacdLine = macdLine(i)
                result.SignalLine = signalLine(i)

                If macdLine(i).HasValue AndAlso signalLine(i).HasValue Then
                    result.Histogram = macdLine(i).Value - signalLine(i).Value
                    ' Populamos AdditionalValues para serialización MCP
                    result.AdditionalValues("Signal") = signalLine(i)
                    result.AdditionalValues("Histogram") = result.Histogram
                End If

                results.Add(result)
                i += 1
            Loop

            Return results
        End Function

        ''' <summary>
        ''' Helper interno: calcula EMA sobre los cierres de las velas.
        ''' Retorna array de None donde no hay suficientes datos y Decimal donde sí.
        ''' </summary>
        Private Shared Function CalcEMA(candleList As List(Of CandleModel), period As Integer) As Decimal()
            Dim n As Integer = candleList.Count
            Dim ema(n - 1) As Decimal
            Dim multiplier As Decimal = 2D / (period + 1)

            ' Primer EMA = SMA de los primeros N cierres
            If n < period Then Return ema

            Dim sumInit As Decimal = 0
            Dim k As Integer = 0
            Do While k < period
                sumInit += candleList(k).ClosePrice
                k += 1
            Loop
            ema(period - 1) = sumInit / period

            ' EMA recursiva para el resto
            Dim i As Integer = period
            Do While i < n
                ema(i) = (candleList(i).ClosePrice * multiplier) + (ema(i - 1) * (1D - multiplier))
                i += 1
            Loop

            Return ema
        End Function
    End Class

End Namespace
