' ============================================================
' CondicionCompilador.vb
'
' Compila un arbol de Condicion (definido en JSON) a un
' delegado Func(Of EvalContext, Boolean) que puede evaluarse
' eficientemente en cada vela sin re-parsear el JSON.
'
' Namespace: WpfDerivClientVB.EstrategiasDinamicas
' ============================================================
Imports System
Imports System.Collections.Generic
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB.EstrategiasDinamicas

    ''' <summary>
    ''' Compila objetos <see cref="Condicion"/> a delegados Func pre-compilados.
    ''' Soporta arboles AND/OR recursivos y referencias cruzadas entre campos.
    ''' </summary>
    Public Module CondicionCompilador

        ''' <summary>
        ''' Compila una condicion (simple o compuesta) a un delegado.
        ''' Si la condicion es Nothing, retorna siempre False.
        ''' Si hay un error en la evaluacion (clave no encontrada, etc.), retorna False.
        ''' </summary>
        Public Function Compilar(cond As Condicion) As Func(Of EvalContext, Boolean)
            If cond Is Nothing Then Return Function(ctx) False

            If cond.EsCompuesto Then
                Return CompilarCompuesto(cond)
            Else
                Return CompilarHoja(cond)
            End If
        End Function

        ' ────────────────────────────────────────────────────────
        '  Nodo compuesto: AND / OR de sub-condiciones
        ' ────────────────────────────────────────────────────────
        Private Function CompilarCompuesto(cond As Condicion) As Func(Of EvalContext, Boolean)
            Dim hijos As New List(Of Func(Of EvalContext, Boolean))()
            For Each hijo In cond.Condiciones
                hijos.Add(Compilar(hijo))
            Next

            Dim operador = If(cond.Operador, "AND").ToUpperInvariant()

            If operador = "OR" Then
                Return Function(ctx)
                           For Each fn In hijos
                               Try
                                   If fn(ctx) Then Return True
                               Catch
                                   ' ignorar errores en sub-condiciones
                               End Try
                           Next
                           Return False
                       End Function
            Else
                ' AND por defecto
                Return Function(ctx)
                           For Each fn In hijos
                               Try
                                   If Not fn(ctx) Then Return False
                               Catch
                                   Return False
                               End Try
                           Next
                           Return True
                       End Function
            End If
        End Function

        ' ────────────────────────────────────────────────────────
        '  Nodo hoja: campo op valor
        ' ────────────────────────────────────────────────────────
        Private Function CompilarHoja(cond As Condicion) As Func(Of EvalContext, Boolean)
            Dim campo  As String = cond.Campo
            Dim op     As String = If(cond.Op, "").ToLowerInvariant()
            Dim valorJ As JToken = cond.Valor
            Dim factor As Decimal = If(cond.Factor, 1D)

            ' Pre-determinar si el valor es una referencia a otro campo o un literal
            Dim esReferencia As Boolean = False
            Dim referenciaKey As String = ""
            Dim literalVal As Decimal = 0

            If valorJ IsNot Nothing Then
                If valorJ.Type = JTokenType.String Then
                    esReferencia = True
                    referenciaKey = valorJ.ToString()
                Else
                    Try
                        ' JToken no implementa IConvertible directamente — usar ToObject para conversion segura
                        literalVal = valorJ.ToObject(Of Decimal)()
                    Catch
                        ' Si no se puede convertir, usar 0
                    End Try
                End If
            End If

            Return Function(ctx)
                       Try
                           Dim izquierda As Decimal = ctx.GetValor(campo)

                           Dim derecha As Decimal
                           If esReferencia Then
                               derecha = ctx.GetValor(referenciaKey)
                           Else
                               derecha = literalVal
                           End If
                           derecha *= factor

                           Select Case op
                               Case "mayor_que"
                                   Return izquierda > derecha
                               Case "menor_que"
                                   Return izquierda < derecha
                               Case "igual_a"
                                   Return izquierda = derecha
                               Case "mayor_igual"
                                   Return izquierda >= derecha
                               Case "menor_igual"
                                   Return izquierda <= derecha
                               Case Else
                                   Return False
                           End Select
                       Catch
                           ' Clave no encontrada u otro error → condicion no cumplida
                           Return False
                       End Try
                   End Function
        End Function

    End Module

End Namespace
