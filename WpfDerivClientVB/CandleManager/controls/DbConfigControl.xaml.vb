Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Threading
Imports Npgsql

' ================================================================
'  DbConfigControl — Code-behind
'  Lógica: cargar config, actualizar preview, test async,
'          explorar tablas reales, guardar en settings.json
' ================================================================
Partial Public Class DbConfigControl
    Inherits UserControl

    ' ── Evento: configuración guardada exitosamente ──────────────
    Public Event ConfigSaved(settings As WpfDerivClientVB.DatabaseSettings)

    Private _showPassword As Boolean = False

    ' ── Constructor ──────────────────────────────────────────────
    Public Sub New()
        InitializeComponent()
        CargarConfigGuardada()
        AddHandler txtHost.TextChanged,     AddressOf OnFieldChanged
        AddHandler txtPort.TextChanged,     AddressOf OnFieldChanged
        AddHandler txtDatabase.TextChanged, AddressOf OnFieldChanged
        AddHandler txtUsername.TextChanged, AddressOf OnFieldChanged
        AddHandler txtSchema.TextChanged,   AddressOf OnFieldChanged
    End Sub

    ' ── Carga valores guardados en el formulario ─────────────────
    Private Sub CargarConfigGuardada()
        Dim db = WpfDerivClientVB.SettingsManager.GetDatabase()
        txtHost.Text     = db.Host
        txtPort.Text     = db.Port.ToString()
        txtDatabase.Text = db.Database
        txtUsername.Text = db.Username
        txtSchema.Text   = db.Schema
        pwdPassword.Password = db.Password
        ActualizarPreview()
    End Sub

    ' ── Actualizar preview del connection string ─────────────────
    Private Sub OnFieldChanged(sender As Object, e As TextChangedEventArgs)
        ActualizarPreview()
        ' Si habían tablas mostradas, las ocultamos al cambiar campos
        pnlTablesSection.Visibility = Visibility.Collapsed
    End Sub

    Private Sub ActualizarPreview()
        Dim connStr As String = String.Format(
            "Host={0};Port={1};Database={2};Username={3};Password=***;",
            txtHost.Text, txtPort.Text, txtDatabase.Text, txtUsername.Text)
        txtConnPreview.Text = connStr
    End Sub

    ' ── Toggle mostrar/ocultar password ─────────────────────────
    Private Sub BtnTogglePwd_Click(sender As Object, e As RoutedEventArgs)
        _showPassword = Not _showPassword
        If _showPassword Then
            txtPasswordVisible.Text = pwdPassword.Password
            pwdPassword.Visibility = Visibility.Collapsed
            txtPasswordVisible.Visibility = Visibility.Visible
        Else
            pwdPassword.Password = txtPasswordVisible.Text
            txtPasswordVisible.Visibility = Visibility.Collapsed
            pwdPassword.Visibility = Visibility.Visible
        End If
    End Sub

    ' ── Leer password actual (de cualquier control activo) ───────
    Private Function GetPassword() As String
        Return If(_showPassword, txtPasswordVisible.Text, pwdPassword.Password)
    End Function

    ' ── Construir DatabaseSettings desde el formulario ──────────
    Private Function BuildSettings() As WpfDerivClientVB.DatabaseSettings
        Dim settings As New WpfDerivClientVB.DatabaseSettings()
        settings.Host     = txtHost.Text.Trim()
        settings.Port     = If(Integer.TryParse(txtPort.Text, 0), CInt(txtPort.Text), 5432)
        settings.Database = txtDatabase.Text.Trim()
        settings.Username = txtUsername.Text.Trim()
        settings.Password = GetPassword()
        settings.Schema   = txtSchema.Text.Trim()
        settings.Enabled  = True
        Return settings
    End Function

    ' ================================================================
    '  BOTÓN: PROBAR CONEXIÓN
    ' ================================================================
    Private Async Sub BtnTest_Click(sender As Object, e As RoutedEventArgs)
        MostrarEstado("Probando conexión...", "#4CD6FB", True)
        btnTest.IsEnabled  = False
        btnSave.IsEnabled  = False
        btnExplore.IsEnabled = False

        Dim db = BuildSettings()
        Dim ok As Boolean = False
        Dim mensaje As String = ""

        Try
            Await System.Threading.Tasks.Task.Run(
                Async Function()
                    Using conn As New NpgsqlConnection(db.ToConnectionString())
                        Await conn.OpenAsync()
                        ' Verificar que el schema existe
                        Using cmd As New NpgsqlCommand(
                            "SELECT schema_name FROM information_schema.schemata WHERE schema_name = @s", conn)
                            cmd.Parameters.AddWithValue("s", db.Schema)
                            Dim result = Await cmd.ExecuteScalarAsync()
                            If result Is Nothing OrElse result Is DBNull.Value Then
                                mensaje = "Conexión OK pero el schema '" & db.Schema & "' no existe."
                            Else
                                mensaje = "✅  Conexión exitosa — PostgreSQL " &
                                          conn.PostgreSqlVersion.ToString() &
                                          "  |  Schema: " & db.Schema
                                ok = True
                            End If
                        End Using
                    End Using
                End Function)
        Catch ex As Exception
            mensaje = "❌  Error: " & ex.Message
            ok = False
        End Try

        If ok Then
            MostrarEstado(mensaje, "#00d4aa", False)
        Else
            MostrarEstado(mensaje, "#ff4757", False)
        End If

        btnTest.IsEnabled    = True
        btnSave.IsEnabled    = True
        btnExplore.IsEnabled = True
    End Sub

    ' ================================================================
    '  BOTÓN: EXPLORAR BD (listar tablas del schema)
    ' ================================================================
    Private Async Sub BtnExplore_Click(sender As Object, e As RoutedEventArgs)
        MostrarEstado("Explorando base de datos...", "#4CD6FB", True)
        btnTest.IsEnabled    = False
        btnExplore.IsEnabled = False

        Dim db = BuildSettings()
        Dim tablas As New System.Collections.Generic.List(Of String)()
        Dim errorMsg As String = ""

        Try
            Await System.Threading.Tasks.Task.Run(
                Async Function()
                    Using conn As New NpgsqlConnection(db.ToConnectionString())
                        Await conn.OpenAsync()
                        Dim sql As String =
                            "SELECT table_name " &
                            "FROM information_schema.tables " &
                            "WHERE table_schema = @s " &
                            "ORDER BY table_name;"
                        Using cmd As New NpgsqlCommand(sql, conn)
                            cmd.Parameters.AddWithValue("s", db.Schema)
                            Using reader = Await cmd.ExecuteReaderAsync()
                                Do While Await reader.ReadAsync()
                                    tablas.Add(reader.GetString(0))
                                Loop
                            End Using
                        End Using
                    End Using
                End Function)
        Catch ex As Exception
            errorMsg = ex.Message
        End Try

        btnTest.IsEnabled    = True
        btnExplore.IsEnabled = True

        If errorMsg <> "" Then
            MostrarEstado("❌  Error al explorar: " & errorMsg, "#ff4757", False)
            Return
        End If

        If tablas.Count = 0 Then
            MostrarEstado("Conexión OK — Schema vacío (0 tablas encontradas).", "#f39c12", False)
            pnlTablesSection.Visibility = Visibility.Collapsed
            Return
        End If

        ' Mostrar tablas
        lstTables.ItemsSource = tablas
        pnlTablesSection.Visibility = Visibility.Visible
        MostrarEstado("✅  " & tablas.Count & " tabla(s) encontrada(s) en schema '" & db.Schema & "'",
                      "#00d4aa", False)
    End Sub

    ' ================================================================
    '  BOTÓN: GUARDAR
    ' ================================================================
    Private Sub BtnSave_Click(sender As Object, e As RoutedEventArgs)
        ' Validación básica
        If txtHost.Text.Trim() = "" OrElse txtDatabase.Text.Trim() = "" OrElse
           txtUsername.Text.Trim() = "" OrElse txtPort.Text.Trim() = "" Then
            MostrarEstado("❌  Completa todos los campos requeridos.", "#ff4757", False)
            Return
        End If
        Dim port As Integer
        If Not Integer.TryParse(txtPort.Text.Trim(), port) Then
            MostrarEstado("❌  El puerto debe ser un número entero.", "#ff4757", False)
            Return
        End If

        Dim db = BuildSettings()
        WpfDerivClientVB.SettingsManager.SaveDatabase(db)

        MostrarEstado("💾  Configuración guardada exitosamente en settings.json", "#00d4aa", False)
        RaiseEvent ConfigSaved(db)
    End Sub

    ' ── Helpers de UI ────────────────────────────────────────────
    Private Sub MostrarEstado(msg As String, hexColor As String, showSpinner As Boolean)
        bdrStatus.Visibility    = Visibility.Visible
        txtStatus.Text          = msg
        bdrSpinner.Visibility   = If(showSpinner, Visibility.Visible, Visibility.Collapsed)

        Try
            Dim color = DirectCast(ColorConverter.ConvertFromString(hexColor), Color)
            ellStatus.Fill   = New SolidColorBrush(color)
            txtStatus.Foreground = New SolidColorBrush(color)
        Catch
        End Try
    End Sub

End Class
