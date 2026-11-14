' ============================================================
' EvalContext.vb
'
' Contexto de evaluacion para una estrategia dinamica.
' Contiene los valores calculados de todos los indicadores
' y el precio actual, listos para evaluar condiciones compiladas.
'
' Namespace: WpfDerivClientVB.EstrategiasDinamicas
' ============================================================
Imports System.Collections.Generic

Namespace WpfDerivClientVB.EstrategiasDinamicas

    ''' <summary>
    ''' Mapa de valores calculados por indicadores + precio OHLC actual.
    ''' Las claves son insensibles a mayusculas.
    '''
    ''' Ejemplos de claves:
    '''   "rsi"           → valor RSI actual
    '''   "ema_rapida"    → valor EMA rapida actual
    '''   "macd.Histogram" → histograma MACD actual
    '''   "bb.Upper"       → banda superior de Bollinger
    '''   "close"          → precio de cierre de la vela actual
    '''   "open", "high", "low" → precios OHLC del ultimo candle
    ''' </summary>
    Public Class EvalContext

        Private ReadOnly _valores As New Dictionary(Of String, Decimal)(StringComparer.OrdinalIgnoreCase)

        ''' <summary>
        ''' Registra un valor bajo la clave indicada.
        ''' Sobreescribe si la clave ya existe.
        ''' </summary>
        Public Sub SetValor(clave As String, valor As Decimal)
            _valores(clave) = valor
        End Sub

        ''' <summary>
        ''' Obtiene el valor de la clave indicada.
        ''' Lanza <see cref="KeyNotFoundException"/> si la clave no existe.
        ''' </summary>
        Public Function GetValor(clave As String) As Decimal
            Dim v As Decimal
            If _valores.TryGetValue(clave, v) Then Return v
            Throw New KeyNotFoundException(String.Format("EvalContext: clave '{0}' no encontrada.", clave))
        End Function

        ''' <summary>
        ''' Intenta obtener el valor de la clave indicada.
        ''' Retorna False si la clave no existe; en ese caso valor = 0.
        ''' </summary>
        Public Function TryGetValor(clave As String, ByRef valor As Decimal) As Boolean
            Return _valores.TryGetValue(clave, valor)
        End Function

        ''' <summary>
        ''' Indica si la clave existe en el contexto.
        ''' </summary>
        Public Function ContieneClave(clave As String) As Boolean
            Return _valores.ContainsKey(clave)
        End Function

        ''' <summary>
        ''' Todas las claves registradas en el contexto.
        ''' </summary>
        Public ReadOnly Property Claves As IEnumerable(Of String)
            Get
                Return _valores.Keys
            End Get
        End Property

        ''' <summary>
        ''' Exporta todos los valores del contexto como Dictionary.
        ''' Util para serializar a Datos en EstrategiaResultado.
        ''' </summary>
        Public Function ToDictionary() As Dictionary(Of String, Decimal)
            Return New Dictionary(Of String, Decimal)(_valores, StringComparer.OrdinalIgnoreCase)
        End Function

    End Class

End Namespace
