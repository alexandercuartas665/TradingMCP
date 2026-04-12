' ============================================================
' IEstrategia.vb
' Interfaz base para todas las estrategias de trading.
'
' Patrón idéntico a Indicators/IIndicator.vb:
'   - Una interfaz  → IEstrategia
'   - Un resultado  → EstrategiaResultado
'   - N estrategias → EstrategiaEmaRsi, EstrategiaSeykota ...
'   - Un registry   → EstrategiaRegistry
' ============================================================
Imports System.Collections.Generic

Namespace WpfDerivClientVB.Estrategias

    ''' <summary>
    ''' Resultado normalizado devuelto por cualquier estrategia.
    ''' Contiene la señal principal y datos complementarios.
    ''' </summary>
    Public Class EstrategiaResultado
        ''' <summary>"CALL", "PUT" o "NEUTRAL"</summary>
        Public Property Senal As String = "NEUTRAL"

        ''' <summary>Confianza de 0 a 100.</summary>
        Public Property Confianza As Integer = 0

        ''' <summary>Explicación legible de por qué se generó la señal.</summary>
        Public Property Motivo As String = ""

        ''' <summary>
        ''' Valores intermedios específicos de cada estrategia.
        ''' EmaRsi → "EMA5", "EMA10", "RSI14", "Score"
        ''' Seykota → "Upper", "Lower", "Mid", "ATR14", "SlopeUp", "SlopeDown"
        ''' </summary>
        Public Property Datos As New Dictionary(Of String, Decimal)
    End Class

    ''' <summary>
    ''' Contrato que debe cumplir toda estrategia de trading.
    ''' Las estrategias trabajan sobre una lista de CandleModel
    ''' (ordenada de más antigua a más reciente) y devuelven
    ''' un EstrategiaResultado con señal + confianza + datos.
    ''' </summary>
    Public Interface IEstrategia
        ''' <summary>Nombre corto de la estrategia (usado como clave en el registry).</summary>
        ReadOnly Property Nombre As String

        ''' <summary>Descripción detallada de la lógica y parámetros.</summary>
        ReadOnly Property Descripcion As String

        ''' <summary>
        ''' Analiza las velas y devuelve señal CALL / PUT / NEUTRAL.
        ''' Las velas deben estar ordenadas oldest→newest.
        ''' </summary>
        Function Analizar(candles As IEnumerable(Of CandleModel)) As EstrategiaResultado
    End Interface

End Namespace
