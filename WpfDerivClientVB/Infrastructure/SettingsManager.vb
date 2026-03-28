Imports Newtonsoft.Json
Imports System.IO

Namespace WpfDerivClientVB
    Public Class ChartSettings
        Public Property BullishColor As String = "#26A69A"
        Public Property BearishColor As String = "#EF5350"
        Public Property WickColor As String = "#888888"
        Public Property BackgroundColor As String = "#1E1E2E"
        Public Property GridColor As String = "#2A2A3A"
    End Class

    Public Module SettingsManager
        Private ReadOnly SettingsPath As String = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "settings.json")

        Public Function Load() As ChartSettings
            Try
                If File.Exists(SettingsPath) Then
                    Dim json As String = File.ReadAllText(SettingsPath)
                    Return JsonConvert.DeserializeObject(Of ChartSettings)(json)
                End If
            Catch
            End Try
            Return New ChartSettings()
        End Function

        Public Sub Save(settings As ChartSettings)
            Try
                Dim json As String = JsonConvert.SerializeObject(settings, Formatting.Indented)
                File.WriteAllText(SettingsPath, json)
            Catch
            End Try
        End Sub
    End Module
End Namespace
