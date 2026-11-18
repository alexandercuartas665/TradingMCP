Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB

    ''' <summary>
    ''' Origen de las velas guardadas en la tabla candles (columna platform). Las velas de
    ''' Deriv y las de fuentes externas NUNCA se mezclan: cada backtest elige su fuente.
    ''' Las externas sirven para investigar con mas historia; lo que salga bien se confirma
    ''' en Deriv (forward test), porque sus precios no son identicos.
    ''' </summary>
    Public Module FuentesVelas
        Public Const DERIV As String = "Deriv"
        Public Const DUKASCOPY As String = "Dukascopy"
        Public Const BITSTAMP As String = "Bitstamp"
        Public Const BINANCE As String = "Binance"
        Public Const YAHOO As String = "Yahoo"

        ''' <summary>Todas las fuentes, Deriv primero.</summary>
        Public ReadOnly Todas As String() = {DERIV, DUKASCOPY, BITSTAMP, BINANCE, YAHOO}

        ''' <summary>Las que se bajan de fuera de Deriv (historico externo).</summary>
        Public ReadOnly Externas As String() = {DUKASCOPY, BITSTAMP, BINANCE, YAHOO}

        ''' <summary>Nombre de fuente (sin importar mayusculas) o vacio -> nombre canonico. Otro valor: error.</summary>
        Public Function Normalizar(fuente As String) As String
            If String.IsNullOrWhiteSpace(fuente) Then Return DERIV
            Dim f = fuente.Trim()
            For Each t In Todas
                If String.Equals(t, f, StringComparison.OrdinalIgnoreCase) Then Return t
            Next
            Throw New ArgumentException("Fuente de velas desconocida: " & fuente & " (use " & String.Join(", ", Todas) & ")")
        End Function

        Public Function EsExterna(fuente As String) As Boolean
            Return Externas.Contains(Normalizar(fuente))
        End Function

        ''' <summary>Que tiene cada fuente (para la pantalla y la ayuda de la API).</summary>
        Public Function Descripcion(fuente As String) As String
            Select Case Normalizar(fuente)
                Case DUKASCOPY : Return "Dukascopy: divisas, metales e indices desde 2003 (US100 desde 2011, DAX desde 2013); de 1m a 1d."
                Case BITSTAMP : Return "Bitstamp: cripto contra USD (cryBTCUSD...) desde 2011; 1h, 4h y 1d."
                Case BINANCE : Return "Binance: cripto contra USDT (cryBTCUSD -> BTCUSDT) desde 2017; 1h, 4h y 1d."
                Case YAHOO : Return "Yahoo: indices de contado (OTC_NDX = Nasdaq-100, OTC_GDAXI = DAX...) desde 2002; solo 1d."
            End Select
            Return "Deriv: las velas de la plataforma donde se opera."
        End Function
    End Module

    ''' <summary>Descargador de una fuente externa (Dukascopy, Bitstamp, Binance, Yahoo).</summary>
    Public Interface IDescargaHistorica
        ReadOnly Property Fuente As String
        ReadOnly Property Temporalidades As String()
        ''' <summary>Fecha desde la que tiene datos (inicio por defecto de una descarga).</summary>
        ReadOnly Property DesdePorDefecto As Date
        ReadOnly Property EstaDescargando As Boolean
        Function Soporta(simbolo As String) As Boolean
        Function Estado() As JObject
        Sub Detener()
        Function Iniciar(simbolos As IList(Of String), temporalidades As IList(Of String), desde As Date, hasta As Date) As JObject
        ''' <summary>Pone al dia hasta ayer cada serie ya guardada de la fuente.</summary>
        Function ActualizarTodoAsync(Optional ct As CancellationToken = Nothing) As Task(Of JObject)
    End Interface

    ''' <summary>Registro de los descargadores externos.</summary>
    Public Module DescargasHistoricas
        Public Function Todas() As List(Of IDescargaHistorica)
            Return New List(Of IDescargaHistorica) From {
                DukascopyDescarga.Instancia, DescargaHttpExterna.Bitstamp, DescargaHttpExterna.Binance, DescargaHttpExterna.Yahoo}
        End Function

        ''' <summary>Descargador de la fuente; Nothing para Deriv (se baja con /api/historico).</summary>
        Public Function Para(fuente As String) As IDescargaHistorica
            Dim f = FuentesVelas.Normalizar(fuente)
            Return Todas().FirstOrDefault(Function(d) d.Fuente = f)
        End Function
    End Module

End Namespace
