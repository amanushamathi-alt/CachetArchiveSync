// CAS-032 §8.4 step 5: hands a DotNetStreamReference's bytes to the browser as a file download
// (Microsoft's documented Blazor Server file-download pattern). Nothing is written on the Web server;
// the bytes exist only in the stream and this Blob, whose object URL is revoked immediately.
window.cachetDownload = {
    fromStream: async function (fileName, streamRef) {
        const arrayBuffer = await streamRef.arrayBuffer();
        const blob = new Blob([arrayBuffer]);
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = fileName ?? '';
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        URL.revokeObjectURL(url);
    }
};
