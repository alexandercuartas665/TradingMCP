' ============================================================
' EstrategiaDinamicaModelos.vb
'
' DTOs para el Motor de Estrategias Dinamicas.
' Define la estructura de los archivos JSON de estrategia.
'
' Namespace: WpfDerivClientVB.EstrategiasDinamicas
' ============================================================
Imports System.Collections.Generic
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB.EstrategiasDinamicas

    ''' <summary>
    ''' Configuracion de un indicador tecnico en una estrategia JSON.
    ''' </summary>
    Public Class IndicadorConfig
        ''' <summary>
        ''' Identificador unico del indicador dentro de la estrategia.
        ''' Se usa como clave en EvalContext (ej: "ema_rapida", "rsi", "macd").
        ''' Para indicadores multi-valor, los subcampos quedan como "id.Subcampo".
        ''' </summary>
        <JsonProperty("id")>
        Public Property Id As String = ""

        ''' <summary>
        ''' Tipo de indicador. Debe coincidir con los tipos soportados en IndicadorFabrica.
        ''' Valores: "EMA", "SMA", "WMA", "RSI", "MACD", "BollingerBands", "ATR",
        '''          "DonchianChannel", "Supertrend", "ADX", "CCI", "Momentum",
        '''          "Ichimoku", "ParabolicSAR".
        ''' </summary>
        <JsonProperty("tipo")>
        Public Property Tipo As String = ""

        ''' <summary>
        ''' Parametros especificos del indicador.
        ''' Ejemplo: {"periodo": 14} para RSI, {"rapido": 12, "lento": 26, "senal": 9} para MACD.
        ''' </summary>
        <JsonProperty("params")>
        Public Property Params As New Dictionary(Of String, Object)
    End Class

    ''' <summary>
    ''' Representa una condicion simple o compuesta en la logica de la estrategia.
    '''
    ''' Nodo hoja: tiene Campo + Op + Valor.
    ''' Nodo compuesto: tiene Operador + Condiciones (lista de sub-condiciones).
    ''' </summary>
    Public Class Condicion
        ''' <summary>
        ''' Campo del EvalContext a evaluar. Solo en nodos hoja.
        ''' Puede ser clave simple ("rsi") o compuesta ("macd.Histogram").
        ''' </summary>
        <JsonProperty("campo")>
        Public Property Campo As String = ""

        ''' <summary>
        ''' Operador de comparacion. Solo en nodos hoja.
        ''' Valores: "mayor_que", "menor_que", "igual_a", "mayor_igual", "menor_igual".
        ''' </summary>
        <JsonProperty("op")>
        Public Property Op As String = ""

        ''' <summary>
        ''' Valor de comparacion. Solo en nodos hoja.
        ''' Puede ser un numero (45.0) o un string con el nombre de otro campo ("ema_lenta").
        ''' </summary>
        <JsonProperty("valor")>
        Public Property Valor As JToken = Nothing

        ''' <summary>
        ''' Operador logico para nodos compuestos: "AND" (default) u "OR".
        ''' </summary>
        <JsonProperty("operador")>
        Public Property Operador As String = "AND"

        ''' <summary>
        ''' Sub-condiciones para nodos compuestos.
        ''' </summary>
        <JsonProperty("condiciones")>
        Public Property Condiciones As List(Of Condicion) = Nothing

        ''' <summary>
        ''' Indica si este nodo es compuesto (tiene sub-condiciones).
        ''' </summary>
        <JsonIgnore>
        Public ReadOnly Property EsCompuesto As Boolean
            Get
                Return Condiciones IsNot Nothing AndAlso Condiciones.Count > 0
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Ajuste dinamico de confianza segun condiciones adicionales.
    ''' Si la condicion "Si" es verdadera, se suma "Sumar" puntos a la confianza.
    ''' </summary>
    Public Class AjusteConfianza
        ''' <summary>Condicion que activa el ajuste.</summary>
        <JsonProperty("si")>
        Public Property Si As Condicion = Nothing

        ''' <summary>Puntos a sumar a la confianza base (puede ser negativo).</summary>
        <JsonProperty("sumar")>
        Public Property Sumar As Integer = 0
    End Class

    ''' <summary>
    ''' Definicion completa de una estrategia dinamica deserializada desde JSON.
    ''' </summary>
    Public Class EstrategiaDinamicaDef
        ''' <summary>Nombre corto de la estrategia (usado como clave en el registry).</summary>
        <JsonProperty("nombre")>
        Public Property Nombre As String = ""

        ''' <summary>Descripcion legible de la estrategia y su logica.</summary>
        <JsonProperty("descripcion")>
        Public Property Descripcion As String = ""

        ''' <summary>Version del schema (para compatibilidad futura).</summary>
        <JsonProperty("version")>
        Public Property Version As String = "1.0"

        ''' <summary>Lista de indicadores tecnicos que usa esta estrategia.</summary>
        <JsonProperty("indicadores")>
        Public Property Indicadores As New List(Of IndicadorConfig)

        ''' <summary>Condicion que debe cumplirse para emitir senal CALL.</summary>
        <JsonProperty("senal_call")>
        Public Property SenalCall As Condicion = Nothing

        ''' <summary>Condicion que debe cumplirse para emitir senal PUT.</summary>
        <JsonProperty("senal_put")>
        Public Property SenalPut As Condicion = Nothing

        ''' <summary>
        ''' Confianza base emitida cuando se cumple alguna senal (0-100).
        ''' Los ajustes_confianza pueden incrementarla o reducirla.
        ''' </summary>
        <JsonProperty("confianza_base")>
        Public Property ConfianzaBase As Integer = 50

        ''' <summary>Ajustes opcionales a la confianza segun condiciones adicionales.</summary>
        <JsonProperty("ajustes_confianza")>
        Public Property AjustesConfianza As New List(Of AjusteConfianza)

        ''' <summary>Path completo del archivo JSON origen. Asignado por EstrategiasDinamicasRegistry.</summary>
        <JsonIgnore>
        Public Property ArchivoOrigen As String = ""
    End Class

End Namespace
