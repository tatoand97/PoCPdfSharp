# PoCPdfSharp

API de prueba en ASP.NET Core para recibir HTML, sanitizarlo y devolver un PDF generado en memoria usando iText pdfHTML.

## Qué hace

- Expone un endpoint `POST /api/pdf/render`.
- Recibe HTML en JSON.
- Sanitiza etiquetas, atributos, CSS y recursos externos antes de renderizar.
- Genera el PDF completamente en memoria.
- Devuelve el binario con `Content-Type: application/pdf`.
- Registra métricas de validación, sanitización y render.

## Stack

- `.NET 10`
- `ASP.NET Core Minimal API`
- `iText + pdfHTML`
- `HtmlSanitizer`
- `xUnit` para pruebas de integración

## Estructura

- `Program.cs`: configuración de la aplicación y DI.
- `Endpoints/`: definición del endpoint HTTP.
- `Services/`: validación, sanitización y renderizado.
- `Infrastructure/`: manejo global de errores y restricción de recursos remotos.
- `Contracts/`: contratos de request/response internos.
- `Options/`: opciones configurables de render.
- `tests/PoCPdfSharp.Tests/`: pruebas de integración.

## Requisitos

- SDK de `.NET 10`

## Ejecutar localmente

```powershell
dotnet restore
dotnet run
```

Por defecto puedes probar el endpoint con el archivo [`PoCPdfSharp.http`](C:/Users/PC/source/repos/PoCPdfSharp/PoCPdfSharp.http) o con cualquier cliente HTTP.

En entorno de desarrollo también se expone OpenAPI.

## Endpoint

`POST /api/pdf/render`

### Request

```json
{
  "html": "<html><body><h1>PoC HTML to PDF</h1><p>Este PDF se genera en memoria.</p></body></html>",
  "fileName": "documento-prueba",
  "baseUri": "https://example.com/"
}
```

### Campos

- `html`: obligatorio. Contenido HTML a sanitizar y renderizar.
- `fileName`: opcional. Si no se envía, se usa `document.pdf`. Si no termina en `.pdf`, se agrega automáticamente.
- `baseUri`: opcional. Debe ser una URL absoluta `https`. Se usa para resolver recursos relativos, por ejemplo imágenes.

### Respuesta exitosa

- `200 OK`
- `Content-Type: application/pdf`
- El cuerpo contiene el PDF binario.

### Errores

- `400 Bad Request`: request inválido, body ausente o `baseUri` no válida.
- `422 Unprocessable Entity`: el HTML sanitizado no tiene contenido útil o intenta usar recursos/markup no permitido.
- `500 Internal Server Error`: error inesperado durante el render.

Las respuestas de error se devuelven como `application/problem+json` e incluyen `traceId`.

## Reglas de seguridad

Esta PoC ya incorpora varias defensas para reducir riesgos al convertir HTML arbitrario:

- Solo acepta `baseUri` con esquema `https`.
- Sanitiza HTML y CSS antes de renderizar.
- Elimina etiquetas peligrosas como `script`, `iframe`, `form`, `object`, `embed`, `svg`, `meta` y similares.
- Solo permite un conjunto acotado de tags, atributos y propiedades CSS.
- Solo admite imágenes remotas por `https` o `data:` base64.
- Restringe recursos remotos al mismo host de `baseUri` cuando `RestrictToBaseUriHost` está habilitado.
- Rechaza recursos con media types no permitidos.
- Limita tamaño de recursos y tiempo máximo de descarga.

## Configuración

Las opciones viven en `appsettings.json`, sección `PdfRendering`:

```json
"PdfRendering": {
  "MaxResourceBytes": 2097152,
  "ResourceTimeoutSeconds": 10,
  "AllowHttpsResources": true,
  "AllowDataUriImages": true,
  "RestrictToBaseUriHost": true,
  "MaxLayoutPasses": 4096
}
```

### Significado de las opciones

- `MaxResourceBytes`: tamaño máximo permitido por recurso externo.
- `ResourceTimeoutSeconds`: timeout para descargar recursos remotos.
- `AllowHttpsResources`: habilita o bloquea recursos cargados por `https`.
- `AllowDataUriImages`: habilita o bloquea imágenes embebidas en `data:`.
- `RestrictToBaseUriHost`: limita recursos remotos al host de `baseUri`.
- `MaxLayoutPasses`: límite de layout configurado en pdfHTML.

## Pruebas

```powershell
dotnet test
```

Estado verificado localmente: `1` prueba superada.

## Licencia

El repositorio incluye [`LICENSE.txt`](C:/Users/PC/source/repos/PoCPdfSharp/LICENSE.txt) con plantilla MIT, pero todavía tiene placeholders (`[year]`, `[fullname]`) que conviene completar.

Además, este proyecto usa iText/pdfHTML, que tiene condiciones de licenciamiento propias. Antes de usarlo en producción o distribuirlo, revisa el modelo de licencia aplicable a tu caso.
