' ============================================================
' DescargaService.vb
' Servicio centralizado de descarga de datos históricos.
'
' Usos:
'   - MainWindow.Descarga.vb  → formulario UI
'   - TradingApiServer.vb     → endpoints HTTP / MCP
'
' Patrón igual a Indicators/: lógica de negocio desacoplada
' de la UI, accesible desde cualquier capa de la app.
' ============================================================
Imports System.Threading.Tasks

Namespace WpfDerivClientVB

    ''' <summary>
    ''' Estado del job de descarga actual (o el último ejecutado).
    ''' Se devuelve por copia para evitar condiciones de carrera.
    ''' </summary>
    Public Class DescargaJobEstado
        Public Property Symbol         As String  = ""
        Public Property Timeframe      As String  = ""
        Public Property Desde          As String  = ""
        Public Property Hasta          As String  = ""
        Public Property EstaActivo     As Boolean = False
        Public Property VelasGuardadas As Integer = 0
        Public Property Pagina         As Integer = 0
        Public Property UltimoMensaje  As String  = "Sin descarga activa"
        Public Property Completado     As Boolean = False
        Public Property TuvoError      As Boolean = False
        Public Property InicioUtc      As String  = ""
    End Class

    ''' <summary>
    ''' Servicio de descarga histórica reutilizable.
    ''' Inicializar una vez en MainWindow y compartir la referencia
    ''' con TradingApiServer para que los endpoints HTTP/MCP lo usen.
    ''' </summary>
    Public Class DescargaService

        Private ReadOnly _downloader As HistoricalDownloader
        Private ReadOnly _repo       As CandleRepository
        Private Const PLATFORM       As String = "Deriv"

        Private _estado   As New DescargaJobEstado()
        Private ReadOnly _lock As New Object()

        Public Sub New(repo As CandleRepository)
            _repo       = repo
            _downloader = New HistoricalDownloader(repo)
        End Sub

        ' ── Estado ────────────────────────────────────────────
        ''' <summary>Devuelve una copia del estado actual (thread-safe).</summary>
        Public Function ObtenerEstado() As DescargaJobEstado
            SyncLock _lock
                Return New DescargaJobEstado With {
                    .Symbol         = _estado.Symbol,
                    .Timeframe      = _estado.Timeframe,
                    .Desde          = _estado.Desde,
                    .Hasta          = _estado.Hasta,
                    .EstaActivo     = _estado.EstaActivo,
                    .VelasGuardadas = _estado.VelasGuardadas,
                    .Pagina         = _estado.Pagina,
                    .UltimoMensaje  = _estado.UltimoMensaje,
                    .Completado     = _estado.Completado,
                    .TuvoError      = _estado.TuvoError,
                    .InicioUtc      = _estado.InicioUtc
                }
            End SyncLock
        End Function

        ''' <summary>True si hay una descarga en curso.</summary>
        Public ReadOnly Property EstaDescargando As Boolean
            Get
                SyncLock _lock
                    Return _estado.EstaActivo
                End SyncLock
            End Get
        End Property

        ' ── Control ───────────────────────────────────────────
        ''' <summary>Cancela la descarga en curso (si la hay).</summary>
        Public Sub Detener()
            _downloader.StopDownload()
        End Sub

        ' ── Descarga ──────────────────────────────────────────
        ''' <summary>
        ''' Inicia la descarga de velas históricas desde la API de Deriv
        ''' y las guarda en PostgreSQL.
        ''' onProgress es opcional: el formulario lo usa para actualizar la UI;
        ''' las llamadas HTTP lo ignoran y leen ObtenerEstado().
        ''' </summary>
        Public Async Function DescargarAsync(symbol      As String,
                                              timeframe   As String,
                                              fromDate    As DateTime,
                                              toDate      As DateTime,
                                              Optional onProgress As Action(Of DownloadProgress) = Nothing) As Task
            SyncLock _lock
                If _estado.EstaActivo Then
                    Throw New InvalidOperationException(
                        "Ya hay una descarga activa para " & _estado.Symbol & " " & _estado.Timeframe)
                End If
                _estado = New DescargaJobEstado With {
                    .Symbol        = symbol,
                    .Timeframe     = timeframe,
                    .Desde         = fromDate.ToString("yyyy-MM-dd"),
                    .Hasta         = toDate.ToString("yyyy-MM-dd"),
                    .EstaActivo    = True,
                    .UltimoMensaje = "Iniciando conexión con Deriv...",
                    .InicioUtc     = DateTime.UtcNow.ToString("o")
                }
            End SyncLock

            Await _downloader.DownloadAsync(PLATFORM, symbol, timeframe, fromDate, toDate,
                Sub(progress As DownloadProgress)
                    SyncLock _lock
                        _estado.VelasGuardadas = progress.TotalSaved
                        _estado.Pagina         = progress.PageNum
                        _estado.UltimoMensaje  = progress.StatusMsg
                        _estado.TuvoError      = progress.HasError
                        If progress.IsComplete Then
                            _estado.EstaActivo = False
                            _estado.Completado = True
                        End If
                    End SyncLock
                    onProgress?.Invoke(progress)
                End Sub)
        End Function

        ' ── Consulta BD ───────────────────────────────────────
        ''' <summary>Devuelve las últimas N velas almacenadas en BD.</summary>
        Public Async Function ConsultarDatosAsync(symbol    As String,
                                                   timeframe As String,
                                                   Optional limit As Integer = 100) As Task(Of List(Of CandleModel))
            Return Await _repo.GetCandlesAsync(PLATFORM, symbol, timeframe, limit:=limit)
        End Function

        ''' <summary>Cuenta cuántas velas hay en BD para symbol+timeframe.</summary>
        Public Async Function ContarRegistrosAsync(symbol    As String,
                                                    timeframe As String) As Task(Of Long)
            Return Await _repo.CountAsync(PLATFORM, symbol, timeframe)
        End Function

    End Class

End Namespace
