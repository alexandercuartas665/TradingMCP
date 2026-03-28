Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Resultado extendido de Bollinger Bands con los tres valores de banda.
    ''' Hereda de <see cref="IndicatorResult"/> para compatibilidad con IIndicator.Calculate.
    ''' El campo <see cref="IndicatorResult.IndicatorValue"/> almacena la banda central (SMA).
    ''' Los valores adicionales se exponen también en AdditionalValues para serialización MCP.
    ''' </summary>
    Public Class BollingerBandResult
        Inherits IndicatorResult

        ''' <summary>Banda superior = Middle + (multiplier * StdDev).</summary>
        Public Property Upper As Nullable(Of Decimal)

        ''' <summary>Banda central = SMA(period) de los cierres.</summary>
        Public Property Middle As Nullable(Of Decimal)
            Get
                Return IndicatorValue
            End Get
            Set(v As Nullable(Of Decimal))
                IndicatorValue = v
            End Set
        End Property

        ''' <summary>Banda inferior = Middle - (multiplier * StdDev).</summary>
        Public Property Lower As Nullable(Of Decimal)

        ''' <summary>Ancho de banda normalizado = (Upper - Lower) / Middle * 100.</summary>
        Public Property BandWidth As Nullable(Of Decimal)

        ''' <summary>Desviación estándar utilizada en el cálculo.</summary>
        Public Property StdDev As Nullable(Of Decimal)
    End Class

    ''' <summary>
    ''' Indicador de Volatilidad: Bandas de Bollinger.
    ''' Desarrollado por John Bollinger (1980s).
    '''
    ''' Consiste en tres bandas alrededor de una media móvil:
    '''   - Banda Central : SMA(period)
    '''   - Banda Superior: SMA + multiplier × σ (desviación estándar)
    '''   - Banda Inferior: SMA - multiplier × σ
    '''
    ''' Interpretación típica (period=20, multiplier=2):
    '''   - ~95% de los precios caen dentro de las bandas.
    '''   - Precio toca banda superior → posible sobrecompra.
    '''   - Precio toca banda inferior → posible sobreventa.
    '''   - BandWidth bajo → volatilidad comprimida (breakout inminente).
    '''   - BandWidth alto → mercado en expansión de volatilidad.
    ''' </summary>
    Public Class BollingerBands
        Implements IIndicator

        Private ReadOnly _period As Integer
        Private ReadOnly _multiplier As Decimal

        ''' <param name="period">Períodos de la SMA central. Estándar: 20.</param>
        ''' <param name="multiplier">Multiplicador de desviación estándar. Estándar: 2.0.</param>
        Public Sub New(Optional period As Integer = 20, Optional multiplier As Decimal = 2D)
            _period = period
            _multiplier = multiplier
        End Sub

        ''' <inheritdoc/>
        Public ReadOnly Property Name As String Implements IIndicator.Name
            Get
                Return String.Format("BB({0},{1})", _period, _multiplier)
            End Get
        End Property

        ''' <inheritdoc/>
        Public ReadOnly Property Description As String Implements IIndicator.Description
            Get
                Return String.Format(
                    "Bandas de Bollinger: SMA({0}) ± {1}σ. " &
                    "Banda central = SMA de {0} cierres. " &
                    "Bandas externas = ±{1} desviaciones estándar. " &
                    "~95% de precios dentro de las bandas con multiplier=2. " &
                    "BandWidth = (Upper-Lower)/Middle×100 indica nivel de volatilidad.", _period, _multiplier)
            End Get
        End Property

        ''' <inheritdoc/>
        Public ReadOnly Property Category As String Implements IIndicator.Category
            Get
                Return "Volatility"
            End Get
        End Property

        ''' <summary>
        ''' Implementación IIndicator. Retorna List(Of IndicatorResult) donde cada elemento
        ''' es en realidad un <see cref="BollingerBandResult"/> (polimorfismo).
        ''' Para acceso tipado, use <see cref="CalculateBands"/>.
        ''' </summary>
        Public Function Calculate(candles As IEnumerable(Of CandleModel)) As List(Of IndicatorResult) Implements IIndicator.Calculate
            Return CalculateBands(candles).Cast(Of IndicatorResult).ToList()
        End Function

        ''' <summary>
        ''' Calcula las Bandas de Bollinger. Proporciona acceso tipado a Upper, Middle,
        ''' Lower, BandWidth y StdDev por vela.
        ''' Los primeros (period-1) resultados tienen todos los valores = Nothing.
        ''' </summary>
        Public Function CalculateBands(candles As IEnumerable(Of CandleModel)) As List(Of BollingerBandResult)
            Dim candleList As List(Of CandleModel) = candles.ToList()
            Dim results As New List(Of BollingerBandResult)(candleList.Count)
            Dim labelStr As String = Me.Name

            Dim idx As Integer = 0
            Do While idx < candleList.Count
                Dim item As CandleModel = candleList(idx)
                Dim result As New BollingerBandResult() With {
                    .Epoch = item.Epoch,
                    .OpenTime = item.OpenTime,
                    .Label = labelStr
                }

                If idx < _period - 1 Then
                    result.IndicatorValue = Nothing ' Middle = Nothing
                    result.Upper = Nothing
                    result.Lower = Nothing
                    result.BandWidth = Nothing
                    result.StdDev = Nothing
                Else
                    ' Calcular SMA (middle)
                    Dim total As Decimal = 0
                    Dim k As Integer = idx - _period + 1
                    Do While k <= idx
                        total += candleList(k).ClosePrice
                        k += 1
                    Loop
                    Dim middle As Decimal = total / _period

                    ' Calcular varianza poblacional y desviación estándar
                    Dim sumSqDiff As Decimal = 0
                    k = idx - _period + 1
                    Do While k <= idx
                        Dim diff As Decimal = candleList(k).ClosePrice - middle
                        sumSqDiff += diff * diff
                        k += 1
                    Loop
                    Dim variance As Decimal = sumSqDiff / _period
                    Dim stdDev As Decimal = CDec(Math.Sqrt(CDbl(variance)))

                    Dim upper As Decimal = middle + _multiplier * stdDev
                    Dim lower As Decimal = middle - _multiplier * stdDev
                    Dim bandWidth As Decimal = If(middle <> 0, (upper - lower) / middle * 100D, 0D)

                    result.Middle = middle
                    result.Upper = upper
                    result.Lower = lower
                    result.StdDev = stdDev
                    result.BandWidth = bandWidth

                    ' Populamos AdditionalValues para serialización MCP
                    result.AdditionalValues("Upper") = upper
                    result.AdditionalValues("Lower") = lower
                    result.AdditionalValues("StdDev") = stdDev
                    result.AdditionalValues("BandWidth") = bandWidth
                End If

                results.Add(result)
                idx += 1
            Loop

            Return results
        End Function
    End Class

End Namespace
