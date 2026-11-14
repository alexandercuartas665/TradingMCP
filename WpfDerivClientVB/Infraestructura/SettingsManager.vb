Imports Newtonsoft.Json
Imports System.IO

Namespace WpfDerivClientVB

    ' ── ChartSettings ──────────────────────────────────────────────
    Public Class ChartSettings
        Public Property BullishColor As String = "#26A69A"
        Public Property BearishColor As String = "#EF5350"
        Public Property WickColor As String = "#888888"
        Public Property BackgroundColor As String = "#1E1E2E"
        Public Property GridColor As String = "#2A2A3A"
    End Class

    ' ── DatabaseSettings ───────────────────────────────────────────
    Public Class DatabaseSettings
        Public Property Host As String = "localhost"
        Public Property Port As Integer = 5432
        Public Property Database As String = "traiding_db"
        Public Property Username As String = "postgres"
        Public Property Password As String = ""
        Public Property Schema As String = "public"
        Public Property Enabled As Boolean = False        ' False = no hay config guardada aún

        ''' <summary>Construye el connection string de Npgsql.</summary>
        Public Function ToConnectionString() As String
            Return String.Format(
                "Host={0};Port={1};Database={2};Username={3};Password={4};" &
                "Pooling=true;Minimum Pool Size=1;Maximum Pool Size=10;Connect Timeout=5;",
                Host, Port, Database, Username, Password)
        End Function
    End Class

    ' ── AppSettings (raíz del JSON) ────────────────────────────────
    Public Class AppSettings
        Public Property Chart As ChartSettings = New ChartSettings()
        Public Property Database As DatabaseSettings = New DatabaseSettings()
    End Class

    ' ── SettingsManager (Module) ───────────────────────────────────
    Public Module SettingsManager

        Private ReadOnly SettingsPath As String = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "settings.json")

        Private _cached As AppSettings = Nothing

        ''' <summary>Carga la configuración completa (con caché en memoria).</summary>
        Public Function Load() As AppSettings
            If _cached IsNot Nothing Then Return _cached
            Try
                If File.Exists(SettingsPath) Then
                    Dim json As String = File.ReadAllText(SettingsPath)
                    _cached = JsonConvert.DeserializeObject(Of AppSettings)(json)
                    If _cached Is Nothing Then _cached = New AppSettings()
                    Return _cached
                End If
            Catch
            End Try
            _cached = New AppSettings()
            Return _cached
        End Function

        ''' <summary>Guarda la configuración y limpia la caché.</summary>
        Public Sub Save(settings As AppSettings)
            Try
                Dim json As String = JsonConvert.SerializeObject(settings, Formatting.Indented)
                File.WriteAllText(SettingsPath, json)
                _cached = settings   ' actualizar caché
            Catch
            End Try
        End Sub

        ''' <summary>Shortcut: obtiene solo DatabaseSettings.</summary>
        Public Function GetDatabase() As DatabaseSettings
            Return Load().Database
        End Function

        ''' <summary>Shortcut: guarda solo DatabaseSettings (sin tocar ChartSettings).</summary>
        Public Sub SaveDatabase(db As DatabaseSettings)
            Dim all = Load()
            all.Database = db
            Save(all)
        End Sub

        ''' <summary>Shortcut legacy: obtiene solo ChartSettings.</summary>
        Public Function GetChart() As ChartSettings
            Return Load().Chart
        End Function

    End Module

End Namespace
