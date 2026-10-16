#pragma once

// ---------------------------------------------------------------------------
// The Collector texts this build knows, generated from
// contracts/collector-texts.json into CollectorTextCatalog.g.cpp by
// tools/collector-texts/generate.py. CollectorText.h renders them.
// ---------------------------------------------------------------------------

namespace mr::collector_text {

struct CatalogueEntry
{
    /// What QT_TRANSLATE_NOOP3 yields: the Simplified Chinese source text and
    /// the disambiguation, which is the key.
    struct Source
    {
        const char *text;
        const char *key;
    } source;
    /// "name:kind,name:kind", in the order of the catalogue.
    const char *args;
};

const CatalogueEntry *catalogueBegin();
const CatalogueEntry *catalogueEnd();

} // namespace mr::collector_text
