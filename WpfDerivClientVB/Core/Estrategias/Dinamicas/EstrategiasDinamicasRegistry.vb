' ============================================================
' EstrategiasDinamicasRegistry.vb
'
' Registro central de estrategias dinamicas JSON.
' Carga todos los archivos .json de un directorio, los compila
' a EstrategiaJson y mantiene un cache. Usa FileSystemWatcher
' para recargar automaticamente cuando se modifica un archivo.
'
' Namespace: WpfDerivClientVB.EstrategiasDinamicas
' ============================================================
Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB.EstrategiasDinamicas

    ''' <summary>
    ''' Registry de estrategias JSON. Thread-safe para lecturas concurrentes.
    ''' Inicializar con <see cref="Inicializar"/> al arrancar la aplicacion.
    ''' </summary>
    Public Module EstrategiasDinamicasRegistry

        Private _directorio As String = ""
        Private _cache As New Dictionary(Of String, EstrategiaJson)(StringComparer.OrdinalIgnoreCase)
        Private _watcher As FileSystemWatcher = Nothing
        Private ReadOnly _lock As New Object()

        ' ── Inicializacion ───────────────────────────────────────────────
        ''' <summary>
        ''' Inicializa el registry cargando todos los .json del directorio indicado.
        ''' Si el directorio no existe, lo crea.
        ''' Configura FileSystemWatcher para recarga automatica.
        ''' </summary>
        Public Sub Inicializar(Optional directorio As String = "")
            SyncLock _lock
                Dim dirEfectivo = directorio
                If String.IsNullOrWhiteSpace(dirEfectivo) Then
                    dirEfectivo = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "EstrategiasDinamicas", "estrategias")
                End If

                ' Si ya se inicializo con el mismo directorio, solo recargar cache
                If _directorio.Equals(dirEfectivo, StringComparison.OrdinalIgnoreCase) AndAlso _watcher IsNot Nothing Then
                    CargarTodas()
                    Return
                End If

                _directorio = dirEfectivo

                If Not Directory.Exists(_directorio) Then
                    Try
                        Directory.CreateDirectory(_directorio)
                    Catch ex As Exception
                        AppLogger.Warn("[EstrategiasRegistry] No se pudo crear directorio: " & ex.Message)
                    End Try
                End If

                CargarTodas()
                ConfigurarWatcher()
            End SyncLock
        End Sub

        ' ── Acceso al catalogo ───────────────────────────────────────────
        ''' <summary>Retorna todas las estrategias cargadas como lista.</summary>
        Public Function GetAll() As List(Of EstrategiaJson)
            SyncLock _lock
                Return _cache.Values.ToList()
            End SyncLock
        End Function

        ''' <summary>
        ''' Busca una estrategia por nombre (insensible a mayusculas).
        ''' Retorna Nothing si no existe.
        ''' </summary>
        Public Function GetByNombre(nombre As String) As EstrategiaJson
            SyncLock _lock
                Dim result As EstrategiaJson = Nothing
                _cache.TryGetValue(nombre, result)
                Return result
            End SyncLock
        End Function

        ''' <summary>
        ''' Retorna el catalogo de estrategias como JArray.
        ''' Cada entrada tiene: nombre, descripcion, version, archivo_origen.
        ''' </summary>
        Public Function GetCatalog() As JArray
            SyncLock _lock
                Dim arr As New JArray()
                For Each est In _cache.Values
                    arr.Add(New JObject From {
                        {"nombre",         est.Nombre},
                        {"descripcion",    est.Descripcion},
                        {"version",        est.Definicion.Version},
                        {"archivo_origen", Path.GetFileName(est.Definicion.ArchivoOrigen)}
                    })
                Next
                Return arr
            End SyncLock
        End Function

        ''' <summary>
        ''' Retorna el directorio de estrategias actualmente configurado.
        ''' </summary>
        Public ReadOnly Property Directorio As String
            Get
                Return _directorio
            End Get
        End Property

        ''' <summary>
        ''' Fuerza una recarga de todos los archivos JSON del directorio.
        ''' </summary>
        Public Sub Recargar()
            SyncLock _lock
                CargarTodas()
            End SyncLock
        End Sub

        ' ── Carga interna ────────────────────────────────────────────────
        Private Sub CargarTodas()
            Dim nuevaCache As New Dictionary(Of String, EstrategiaJson)(StringComparer.OrdinalIgnoreCase)

            If Not Directory.Exists(_directorio) Then
                _cache = nuevaCache
                Return
            End If

            Dim archivos = Directory.GetFiles(_directorio, "*.json")
            For Each archivo In archivos
                Try
                    Dim json = File.ReadAllText(archivo, System.Text.Encoding.UTF8)
                    Dim def = JsonConvert.DeserializeObject(Of EstrategiaDinamicaDef)(json)
                    If def Is Nothing OrElse String.IsNullOrWhiteSpace(def.Nombre) Then
                        AppLogger.Warn(String.Format("[EstrategiasRegistry] Archivo sin nombre: {0}", archivo))
                        Continue For
                    End If
                    def.ArchivoOrigen = archivo
                    Dim estrategia As New EstrategiaJson(def)
                    nuevaCache(def.Nombre) = estrategia
                    AppLogger.Info(String.Format("[EstrategiasRegistry] Cargada: {0} ({1})", def.Nombre, Path.GetFileName(archivo)))
                Catch ex As Exception
                    AppLogger.Warn(String.Format("[EstrategiasRegistry] Error cargando '{0}': {1}",
                                                  Path.GetFileName(archivo), ex.Message))
                End Try
            Next

            _cache = nuevaCache
            AppLogger.Info(String.Format("[EstrategiasRegistry] {0} estrategias cargadas desde '{1}'.",
                                        _cache.Count, _directorio))
        End Sub

        Private Sub ConfigurarWatcher()
            If _watcher IsNot Nothing Then
                Try : _watcher.Dispose() : Catch : End Try
                _watcher = Nothing
            End If

            If Not Directory.Exists(_directorio) Then Return

            Try
                _watcher = New FileSystemWatcher(_directorio, "*.json") With {
                    .NotifyFilter = NotifyFilters.LastWrite Or NotifyFilters.FileName Or NotifyFilters.CreationTime,
                    .EnableRaisingEvents = True
                }
                AddHandler _watcher.Changed, AddressOf OnArchivoChanged
                AddHandler _watcher.Created, AddressOf OnArchivoChanged
                AddHandler _watcher.Deleted, AddressOf OnArchivoChanged
                AddHandler _watcher.Renamed, AddressOf OnArchivoRenamed
            Catch ex As Exception
                AppLogger.Warn("[EstrategiasRegistry] FileSystemWatcher no disponible: " & ex.Message)
            End Try
        End Sub

        Private Sub OnArchivoChanged(sender As Object, e As FileSystemEventArgs)
            AppLogger.Info(String.Format("[EstrategiasRegistry] Cambio detectado: {0}. Recargando...", e.Name))
            SyncLock _lock
                CargarTodas()
            End SyncLock
        End Sub

        Private Sub OnArchivoRenamed(sender As Object, e As RenamedEventArgs)
            AppLogger.Info(String.Format("[EstrategiasRegistry] Renombrado: {0} → {1}. Recargando...", e.OldName, e.Name))
            SyncLock _lock
                CargarTodas()
            End SyncLock
        End Sub

    End Module

End Namespace
