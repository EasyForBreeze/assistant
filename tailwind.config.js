/** @type {import('tailwindcss').Config} */
module.exports = {
    content: [
        "./Pages/**/*.{cshtml,razor}",
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
