/** @type {import('tailwindcss').Config} */
module.exports = {
    content: [
        "./Pages/**/*.{cshtml,razor}",
        "./Pages/**/*.cs",
        "./wwwroot/js/**/*.js",
        "./Services/**/*.cs",
        "./KeyCloak/**/*.cs",
        "./Interfaces/**/*.cs",
        "./Program.cs"
    ],
    theme: {
        extend: {}
    }
};
