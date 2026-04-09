# PoCPdfSharp

PoC de `HTML -> PDF` sobre una `ASP.NET Core Minimal API` en `.NET 10`, manteniendo el flujo actual:

`request JSON -> validación -> sanitización -> render HTML->PDF en memoria -> respuesta binaria application/pdf`

La implementación usa `iText` actual con `pdfHTML`, sanitiza con `HtmlSanitizer`, restringe recursos externos y devuelve siempre el PDF binario, nunca base64.

## Qué hace

- Expone `POST /api/pdf/render`.
- Recibe un JSON con `html`, `fileName` y `baseUri`.
- Valida el request y normaliza el nombre del archivo.
- Sanitiza HTML, atributos, CSS y contenido activo.
- Renderiza el PDF completamente en memoria.
- Devuelve `200 OK` con `Content-Type: application/pdf`.
- Devuelve errores consistentes como `application/problem+json` con `traceId`.

## Stack y paquetes clave

- `.NET 10`
- `ASP.NET Core Minimal API`
- `itext` `9.6.0`
- `itext.pdfhtml` `6.3.2`
- `itext.bouncy-castle-adapter` `9.6.0`
- `HtmlSanitizer` `9.0.892`
- `xUnit v3` + `Microsoft.AspNetCore.Mvc.Testing`

## Estructura

- `Program.cs`: composición de DI, logging, `ProblemDetails`, opciones y pipeline HTTP.
- `Endpoints/`: endpoint `POST /api/pdf/render`.
- `Services/`: validación, sanitización y render HTML->PDF.
- `Infrastructure/`: exception handler, supresión selectiva de diagnósticos y política de recursos externos.
- `Contracts/`: request y resultados internos del pipeline.
- `Options/`: opciones configurables de renderizado.
- `tests/PoCPdfSharp.Tests/`: pruebas de integración reales del endpoint.

## Ejecutar

### Restore

```powershell
dotnet restore
```

### Build

```powershell
dotnet build PoCPdfSharp.slnx
```

### Run

```powershell
dotnet run --project PoCPdfSharp.csproj
```

### Test

```powershell
dotnet test PoCPdfSharp.slnx
```

## Endpoint

`POST /api/pdf/render`

### Payload

```json
{
  "html": "<html><body><h1>PoC HTML to PDF</h1><p>Este PDF se genera en memoria.</p></body></html>",
  "fileName": "documento.pdf",
  "baseUri": "https://example.com/"
}
```

### Campos

- `html`: obligatorio.
- `fileName`: opcional. Si no existe o queda inválido tras la normalización, se usa `document.pdf`.
- `baseUri`: opcional. Debe ser una URL absoluta `https` y no puede incluir user info.

## Ejemplos de uso

### curl

```bash
curl -X POST "http://localhost:5134/api/pdf/render" \
  -H "Content-Type: application/json" \
  -H "Accept: application/pdf" \
  --data-raw '{
    "html":"<html><body><h1>Reporte</h1><p>PDF en memoria.</p></body></html>",
    "fileName":"reporte-demo",
    "baseUri":"https://example.com/"
  }' \
  --output reporte-demo.pdf
```

### HTTP file

El repo incluye [`PoCPdfSharp.http`](./PoCPdfSharp.http) para pruebas rápidas desde el IDE.

## Respuestas

### 200 OK

- `Content-Type: application/pdf`
- cuerpo binario del PDF

### 400 Bad Request

Se usa para request inválido, por ejemplo:

- `html` ausente o vacío
- `baseUri` no absoluta o no `https`
- `baseUri` con user info
- JSON mal formado

### 422 Unprocessable Entity

Se usa cuando el HTML sanitizado o sus recursos ya no son válidos para render seguro, por ejemplo:

- el HTML queda sin contenido útil
- un recurso externo viola la política de seguridad
- un recurso `https` no tiene `baseUri` de referencia cuando `RestrictToBaseUriHost=true`
- una imagen remota excede tamaño, timeout o media type permitido
- una imagen `data:` usa media type no permitido o payload inválido

### 500 Internal Server Error

- error inesperado durante el render
- `detail` no expone la excepción interna; se usa `traceId` para correlación

Todas las respuestas de error salen como `application/problem+json` e incluyen `traceId`.

## Configuración

La sección `PdfRendering` vive en [`appsettings.json`](./appsettings.json):

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

### Significado

- `MaxResourceBytes`: límite por recurso externo o `data:` URI.
- `ResourceTimeoutSeconds`: timeout total para recuperar recursos remotos.
- `AllowHttpsResources`: habilita o bloquea imágenes por `https`.
- `AllowDataUriImages`: habilita o bloquea imágenes `data:`.
- `RestrictToBaseUriHost`: restringe recursos externos a la misma autoridad de `baseUri` (`host + puerto`). Si está activo, los recursos `https` requieren un `baseUri` válido para anclar la restricción.
- `MaxLayoutPasses`: límite de layout de `pdfHTML`.

La aplicación valida estas opciones al arranque y falla temprano si la configuración básica es inválida.

## Decisiones técnicas relevantes

- El endpoint mantiene el contrato actual y devuelve binario PDF, no base64.
- El render sigue siendo 100% en memoria.
- No se usa `GC.Collect()` por request; el control de memoria se apoya en disposal determinista.
- El endpoint permanece síncrono a propósito. `pdfHTML` y el retriever de recursos de `iText` exponen interfaces síncronas, así que envolver el render en un `Task.Run` no aporta una asincronía real.
- El borde bloqueante se mantiene lo más acotado posible al convertidor de `iText` y a la recuperación obligatoriamente síncrona de recursos; alrededor de eso se siguen aplicando cancelación, timeout y límites de bytes.
- Se añade una validación previa de URLs de recursos ya sanitizados para que violaciones de política no terminen en un `200` silencioso si `pdfHTML` decide omitir un recurso.
- Los errores controlados se registran una sola vez como `Warning` sin stack trace redundante; los errores inesperados se registran una sola vez como `Error`.
- El `ProblemDetails` usa el servicio estándar del framework y conserva `traceId` en todos los errores.

## Seguridad

La PoC está endurecida para escenarios de HTML arbitrario, dentro de las limitaciones propias de una PoC:

- elimina `script`, `iframe`, `form`, `object`, `embed`, `svg`, `meta` y contenido activo
- elimina handlers inline y CSS peligroso
- bloquea `javascript:`, `vbscript:` y `url(...)` en valores CSS permitidos
- solo permite un subconjunto acotado de tags, atributos y propiedades CSS útiles para PDF
- restringe imágenes remotas a `https` y `data:`
- aplica límite de bytes, timeout y media types permitidos
- bloquea redirects automáticos
- puede restringir recursos externos a la misma autoridad de `baseUri`

## Limitaciones

- No es un motor de navegador completo.
- No ejecuta JavaScript.
- El soporte CSS depende de `pdfHTML`, no de Chromium.
- La PoC no introduce colas, almacenamiento persistente ni cache compartida de recursos entre requests.
- El output está pensado para demostración técnica y endurecimiento básico, no como hardening final de producción.

## Pruebas de integración

Cobertura verificada localmente:

- `POST /api/pdf/render` con HTML válido -> `200`
- `Content-Type = application/pdf`
- body binario no vacío y con cabecera `%PDF-`
- request sin `html` -> `400`
- `baseUri` inválida -> `400`
- `baseUri` con user info -> `400`
- JSON mal formado -> `400`
- HTML inútil tras sanitización -> `422`
- recurso externo no permitido -> `422`
- recurso `https` sin `baseUri` cuando la restricción por host está activa -> `422`
- `data:` URI con media type no permitido -> `422`
- normalización de `fileName`
- `fileName` reservado -> fallback a `document.pdf`
- `traceId` presente en errores

Estado verificado localmente: `12` pruebas superadas.

## Licencia

El repo incluye [`LICENSE.txt`](./LICENSE.txt).

Importante: `iText` y `pdfHTML` tienen licenciamiento dual. Antes de usar esta base fuera de una PoC o en un contexto comercial, revisa el esquema de licencia aplicable a tu caso.
