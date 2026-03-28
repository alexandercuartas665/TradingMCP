Imports System.Collections.Generic

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Resultado genérico producido por cualquier indicador técnico.
    ''' Diseñado para ser serializable a JSON para futura integración MCP.
    '''
    ''' Los indicadores multi-valor (MACD, Bollinger Bands) heredan de esta clase
    ''' y además populan <see cref="AdditionalValues"/> para acceso uniforme.
    ''' </summary>
    Public Class IndicatorResult

        ''' <summary>Timestamp UNIX (segundos desde 1970-01-01 UTC) de la vela asociada.</summary>
        Public Property Epoch As Long

        ''' <summary>Fecha/hora local formateada para display (yyyy-MM-dd HH:mm).</summary>
        Public Property OpenTime As String

        ''' <summary>
        ''' Valor principal del indicador. Es Nothing cuando no hay suficientes
        ''' datos históricos para el cálculo (primeras N-1 velas de un período N).
        ''' </summary>
        Public Property IndicatorValue As Nullable(Of Decimal)

        ''' <summary>Etiqueta descriptiva del indicador (ej: "RSI(14)", "SMA(20)").</summary>
        Public Property Label As String

        ''' <summary>
        ''' Valores adicionales para indicadores multi-valor.
        ''' Clave: nombre del valor (ej: "Signal", "Upper", "Histogram").
        ''' Permite serialización uniforme a JSON para exposición como herramienta MCP.
        ''' </summary>
        Public Property AdditionalValues As Dictionary(Of String, Nullable(Of Decimal))

        Public Sub New()
            AdditionalValues = New Dictionary(Of String, Nullable(Of Decimal))()
        End Sub

        Public Overrides Function ToString() As String
            If IndicatorValue.HasValue Then
                Return String.Format("{0} | {1} = {2:F5}", OpenTime, Label, IndicatorValue.Value)
            Else
                Return String.Format("{0} | {1} = N/A", OpenTime, Label)
            End If
        End Function
    End Class

    ''' <summary>
    ''' Interfaz base que deben implementar todos los indicadores técnicos del sistema.
    '''
    ''' Diseño MCP-Ready:
    '''   - <see cref="Name"/> identifica unívocamente el indicador y sus parámetros.
    '''   - <see cref="Description"/> documenta el indicador para exposición en MCP.
    '''   - <see cref="Category"/> permite agrupar indicadores en el registro MCP.
    '''   - <see cref="Calculate"/> produce resultados serializables a JSON vía AdditionalValues.
    ''' </summary>
    Public Interface IIndicator

        ''' <summary>
        ''' Nombre único del indicador incluyendo parámetros.
        ''' Ejemplo: "RSI(14)", "MACD(12,26,9)", "SMA(20)".
        ''' Este nombre se usa como Tool name en la integración MCP.
        ''' </summary>
        ReadOnly Property Name As String

        ''' <summary>
        ''' Descripción técnica del indicador: qué mide, cómo interpretarlo
        ''' y cuáles son los valores de referencia típicos.
        ''' Se expone como descripción de la herramienta MCP.
        ''' </summary>
        ReadOnly Property Description As String

        ''' <summary>
        ''' Categoría técnica del indicador.
        ''' Valores posibles: "Trend" | "Momentum" | "Volatility" | "Volume"
        ''' Permite filtrar indicadores en el registro MCP por tipo de análisis.
        ''' </summary>
        ReadOnly Property Category As String

        ''' <summary>
        ''' Calcula el indicador sobre una secuencia de velas OHLC.
        ''' REQUISITO: La lista debe estar ordenada de MÁS ANTIGUA a MÁS RECIENTE.
        ''' Donde no hay suficientes datos, IndicatorResult.IndicatorValue = Nothing.
        ''' Los indicadores multi-valor también populan IndicatorResult.AdditionalValues.
        ''' </summary>
        ''' <param name="candles">Velas ordenadas oldest→newest.</param>
        Function Calculate(candles As IEnumerable(Of CandleModel)) As List(Of IndicatorResult)
    End Interface

End Namespace
