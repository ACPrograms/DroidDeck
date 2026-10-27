package coil.request

class ImageRequest {
    class Builder(context: Any?) {
        fun data(data: Any?): Builder = this
        fun size(width: Int, height: Int): Builder = this
        fun scale(scale: Any?): Builder = this
        fun transformations(vararg transformations: Any?): Builder = this
        fun build(): ImageRequest = ImageRequest()
    }
}
